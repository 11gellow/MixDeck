using AudioManager.Core.Models;
using AudioManager.Windows.AudioSessions;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NativeSessionState = NAudio.CoreAudioApi.Interfaces.AudioSessionState;

namespace AudioManager.Windows;

/// <summary>每次操作重新获取设备和会话；所有 NAudio 包装对象在操作结束时释放。</summary>
internal sealed class NAudioBackend(ILogger logger) : IAudioBackend
{
    public AudioEnumeration<AudioOutputDevice> EnumerateOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
        var items = new List<AudioOutputDevice>();
        var issues = new List<AudioReadIssue>();
        for (var index = 0; index < devices.Count; index++)
        {
            try
            {
                using var device = devices[index];
                items.Add(ReadDevice(device, issues));
            }
            catch (Exception exception) { SessionSnapshotReader.AddIssue($"设备索引 {index}", exception, issues); }
        }
        LogIssues(issues);
        return new(items.AsReadOnly(), issues.AsReadOnly());
    }

    public AudioOutputDevice? GetDefaultOutputDevice(AudioDeviceRole role)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, (Role)role, out var device)) return null;
        using (device)
        {
            if (device is null || device.State != DeviceState.Active) return null;
            var issues = new List<AudioReadIssue>();
            var result = ReadDevice(device, issues);
            LogIssues(issues);
            return result;
        }
    }

    public AudioVolume GetMasterVolume(string deviceId) => WithDevice(deviceId, ReadMaster);

    public AudioVolume SetMasterVolume(string deviceId, float level) => WithDevice(deviceId, device =>
    {
        device.AudioEndpointVolume.NotificationGuid = AudioOperationContext.Id;
        device.AudioEndpointVolume.MasterVolumeLevelScalar = level;
        return ReadMaster(device);
    });

    public AudioVolume SetMasterMute(string deviceId, bool isMuted) => WithDevice(deviceId, device =>
    {
        device.AudioEndpointVolume.NotificationGuid = AudioOperationContext.Id;
        device.AudioEndpointVolume.Mute = isMuted;
        return ReadMaster(device);
    });

    public AudioEnumeration<AudioSession> EnumerateSessions(string deviceId) => WithDevice(deviceId, device =>
    {
        var sessions = device.AudioSessionManager.Sessions;
        var items = new List<AudioSession>();
        var issues = new List<AudioReadIssue>();
        for (var index = 0; index < sessions.Count; index++)
        {
            try
            {
                using var session = sessions[index];
                if (session.State == NativeSessionState.AudioSessionStateExpired) continue;
                items.Add(SessionSnapshotReader.Read(deviceId, new NAudioSessionInfo(session), issues));
            }
            catch (Exception exception) { SessionSnapshotReader.AddIssue($"会话索引 {index}", exception, issues); }
        }
        LogIssues(issues);
        return new AudioEnumeration<AudioSession>(items.AsReadOnly(), issues.AsReadOnly());
    });

    public AudioVolume GetSessionVolume(AudioSessionKey key) => WithSession(key, ReadSessionVolume);

    public AudioVolume SetSessionVolume(AudioSessionKey key, float level)
        => SessionVolumeWriter.SetVolume(key, level, AudioOperationContext.Id);

    public AudioVolume SetSessionMute(AudioSessionKey key, bool isMuted)
        => SessionVolumeWriter.SetMute(key, isMuted, AudioOperationContext.Id);

    private static T WithDevice<T>(string deviceId, Func<MMDevice, T> action)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try { device = enumerator.GetDevice(deviceId); }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        { throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "获取输出设备", "输出设备不存在，请重新枚举。", exception); }
        using (device)
        {
            if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "获取输出设备", "该对象不是可用的音频输出设备。");
            return action(device);
        }
    }

    private T WithSession<T>(AudioSessionKey key, Func<AudioSessionControl, T> action) => WithDevice(key.DeviceId, device =>
    {
        var sessions = device.AudioSessionManager.Sessions;
        for (var index = 0; index < sessions.Count; index++)
        {
            AudioSessionControl session;
            try { session = sessions[index]; }
            catch (Exception exception) { logger.LogWarning(exception, "会话索引 {Index} 已失效", index); continue; }
            using (session)
            {
                string instanceId;
                try { instanceId = session.GetSessionInstanceIdentifier; }
                catch (Exception exception) { logger.LogWarning(exception, "会话索引 {Index} 无法读取实例标识", index); continue; }
                if (!string.Equals(instanceId, key.InstanceId, StringComparison.Ordinal)) continue;
                if (session.State == NativeSessionState.AudioSessionStateExpired) break;
                return action(session);
            }
        }
        throw new AudioControlException(AudioErrorCode.SessionUnavailable, "获取指定会话", "会话已退出或失效，请重新枚举。");
    });

    private static AudioVolume ReadMaster(MMDevice device)
        => new(device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);

    private static AudioVolume ReadSessionVolume(AudioSessionControl session)
        => new(session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute);

    private static AudioOutputDevice ReadDevice(MMDevice device, List<AudioReadIssue> issues)
    {
        var id = device.ID;
        string name;
        try { name = device.FriendlyName; }
        catch (Exception exception) { SessionSnapshotReader.AddIssue(id, exception, issues); name = id; }
        return new(id, string.IsNullOrWhiteSpace(name) ? id : name, (AudioDeviceState)device.State);
    }

    private void LogIssues(IEnumerable<AudioReadIssue> issues)
    {
        foreach (var issue in issues)
            logger.LogWarning("音频信息部分读取失败：{ItemId}，{Message}，HRESULT={HResult}", issue.ItemId, issue.Message, issue.NativeErrorCode);
    }

}
