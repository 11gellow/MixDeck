using AudioManager.Core.Models;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NativeSessionState = NAudio.CoreAudioApi.Interfaces.AudioSessionState;

namespace AudioManager.Windows.AudioSessions;

/// <summary>每个受监听设备持有一个 manager；不通过反复 RefreshSessions 接收新会话。</summary>
internal sealed class NAudioSessionSource(string deviceId, ILogger logger) : IAudioSessionSource
{
    private MMDeviceEnumerator? enumerator;
    private MMDevice? device;
    private AudioSessionManager? manager;
    private Action<SessionCreatedData>? onCreated;
    private volatile bool stopped;

    public AudioEnumeration<AudioSession> Initialize(Action<SessionCreatedData> onCreated)
    {
        this.onCreated = onCreated;
        enumerator = new MMDeviceEnumerator();
        try { device = enumerator.GetDevice(deviceId); }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        { throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "监听会话创建", "输出设备不存在，请重新枚举。", exception); }
        if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
            throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "监听会话创建", "该对象不是可用的音频输出设备。");

        manager = device.AudioSessionManager;
        manager.OnSessionCreated += OnSessionCreated;
        var sessions = manager.Sessions;
        // GetCount 是开始接收新会话通知的必要步骤，必须在托管订阅后调用。
        var count = sessions.Count;
        var items = new List<AudioSession>();
        var issues = new List<AudioReadIssue>();
        for (var index = 0; index < count; index++)
        {
            try
            {
                using var session = sessions[index];
                if (session.State == NativeSessionState.AudioSessionStateExpired) continue;
                items.Add(SessionSnapshotReader.Read(deviceId, new NAudioSessionInfo(session), issues));
            }
            catch (Exception exception) { SessionSnapshotReader.AddIssue($"会话索引 {index}", exception, issues); }
        }
        return new(items.AsReadOnly(), issues.AsReadOnly());
    }

    private void OnSessionCreated(object sender, AudioSessionControl session)
    {
        // NAudio 3.2.0 的回调包装不拥有原接口；只在此回调有效期内读取，不跨线程保存。
        using (session)
        {
            if (stopped) return;
            try
            {
                var issues = new List<AudioReadIssue>();
                var snapshot = SessionSnapshotReader.Capture(new NAudioSessionInfo(session), issues);
                onCreated?.Invoke(new(snapshot, issues.AsReadOnly()));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "设备 {DeviceId} 的新会话信息不可读，跳过此次通知", deviceId);
            }
        }
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        if (manager is not null) manager.OnSessionCreated -= OnSessionCreated;
        // MMDevice 拥有 manager 与枚举器；不在系统回调内撤销本机注册。
        try { device?.Dispose(); }
        finally { enumerator?.Dispose(); }
    }
}
