using System.Runtime.InteropServices;
using AudioManager.Core.Models;
using AudioManager.Windows.AudioSessions;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace AudioManager.Windows.Notifications;

/// <summary>NAudio 负责端点音量；窄 COM 投影负责带 EventContext 的会话事件。</summary>
internal sealed class NAudioEndpointSource(string deviceId, ILogger logger, Func<Action, bool> tryPost) : IAudioEndpointSource
{
    private readonly Dictionary<string, Subscription> subscriptions = new(StringComparer.Ordinal);
    private MMDeviceEnumerator? enumerator;
    private MMDevice? device;
    private AudioEndpointVolume? endpointVolume;
    private NativeSessionManager? manager;
    private SessionNotification? sessionNotification;
    private Action<EndpointNotification>? onNotification;
    private volatile bool stopped;

    public EndpointInitialState Initialize(Action<EndpointNotification> onNotification)
    {
        ObjectDisposedException.ThrowIf(stopped, this);
        if (manager is not null) throw new InvalidOperationException("端点监听已初始化。");
        this.onNotification = onNotification;
        enumerator = new MMDeviceEnumerator();
        try { device = enumerator.GetDevice(deviceId); }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        { throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "监听输出设备", "输出设备不存在。", exception); }
        if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
            throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "监听输出设备", "该对象不是可用的音频输出设备。");
        endpointVolume = device.AudioEndpointVolume;
        endpointVolume.OnVolumeNotification += OnMasterVolume;
        var master = new AudioVolume(endpointVolume.MasterVolumeLevelScalar, endpointVolume.Mute);

        manager = SessionComInterop.OpenManager(deviceId);
        sessionNotification = new SessionNotification(this);
        SessionComInterop.Check(manager.Interface.RegisterSessionNotification(sessionNotification));
        using var sessions = manager.Enumerate();
        var count = sessions.Count; // GetCount 启用创建通知；必须在注册之后执行。
        var initial = new List<AudioSession>();
        var issues = new List<AudioReadIssue>();
        for (var index = 0; index < count; index++)
        {
            try
            {
                var captured = AddSession(sessions.GetSession(index));
                if (captured is null) continue;
                var sessionIssues = captured.Issues.ToList();
                initial.Add(SessionSnapshotReader.Read(deviceId, captured.Info, sessionIssues));
                issues.AddRange(sessionIssues);
            }
            catch (Exception exception) { SessionSnapshotReader.AddIssue($"会话索引 {index}", exception, issues); }
        }
        return new(master, new(initial.AsReadOnly(), issues.AsReadOnly()));
    }

    // 本方法取得 session 所有权；重复、过期或读取失败时负责释放。
    private SessionCreatedData? AddSession(NativeSession session)
    {
        Subscription? subscription = null;
        try
        {
            var id = session.InstanceId;
            if (stopped || subscriptions.ContainsKey(id) || session.State == AudioSessionState.Expired)
            { session.Dispose(); return null; }
            subscription = new Subscription(session, id);
            subscription.Callback = new SessionEvents(this, subscription);
            session.Register(subscription.Callback);
            subscription.Registered = true;
            subscriptions.Add(id, subscription);
            var issues = new List<AudioReadIssue>();
            var captured = SessionSnapshotReader.Capture(session, issues);
            if (captured.State == AudioSessionState.Expired)
            { Remove(subscription, notify: false); return null; }
            return new(captured, issues.AsReadOnly());
        }
        catch
        {
            if (subscription is not null) Remove(subscription, notify: false);
            else session.Dispose();
            throw;
        }
    }

    private void OnCreated(IntPtr borrowed)
    {
        if (stopped || borrowed == IntPtr.Zero) return;
        // 必须在回调返回前 AddRef。所属 MTA 接收后转为独占 RCW，再注册会话事件。
        Marshal.AddRef(borrowed);
        if (!Post(() =>
        {
            var session = NativeSession.FromOwnedPointer(borrowed);
            if (stopped) { session.Dispose(); return; }
            try
            {
                var data = AddSession(session);
                if (data is not null) Publish(new EndpointSessionCreated(data));
            }
            catch (Exception exception) { ReportReadFailure(exception); }
        })) Marshal.Release(borrowed);
    }

    private void OnMasterVolume(AudioVolumeNotificationData data)
    {
        try
        {
            var notification = new EndpointMasterVolumeChanged(new(data.MasterVolume, data.Muted), data.EventContext);
            Post(() => { if (!stopped) Publish(notification); });
        }
        catch (Exception exception) { ReportReadFailure(exception); }
    }

    private void OnVolume(Subscription subscription, float level, bool muted, Guid context)
    {
        var volume = new AudioVolume(level, muted);
        Post(() =>
        {
            if (IsCurrent(subscription)) Publish(new EndpointSessionVolumeChanged(subscription.Id, volume, context));
        });
    }

    private void OnState(Subscription subscription, AudioSessionState state)
    {
        Post(() =>
        {
            if (!IsCurrent(subscription)) return;
            if (state == AudioSessionState.Expired) Remove(subscription, notify: true);
            else Publish(new EndpointSessionStateChanged(subscription.Id, state));
        });
    }

    private void OnDisconnected(Subscription subscription, int reason)
    {
        Post(() =>
        {
            if (!IsCurrent(subscription)) return;
            Remove(subscription, notify: true);
            if (reason is 0 or 1 or 2)
                Publish(new EndpointInvalidated($"音频会话因设备/服务/格式变化断开（{reason}）。"));
        });
    }

    private bool IsCurrent(Subscription subscription) => !stopped
        && subscriptions.TryGetValue(subscription.Id, out var current) && ReferenceEquals(current, subscription);

    private void Remove(Subscription subscription, bool notify)
    {
        if (subscriptions.TryGetValue(subscription.Id, out var current) && ReferenceEquals(current, subscription))
            subscriptions.Remove(subscription.Id);
        try
        {
            if (subscription.Registered)
            {
                subscription.Registered = false;
                subscription.Session.Unregister(subscription.Callback!);
            }
        }
        catch (Exception exception) { logger.LogWarning(exception, "解绑会话 {InstanceId} 事件失败", subscription.Id); }
        finally { subscription.Session.Dispose(); }
        if (notify) Publish(new EndpointSessionRemoved(subscription.Id));
    }

    private bool Post(Action action)
    {
        try
        {
            return tryPost(() =>
            {
                try { action(); }
                catch (Exception exception) { ReportReadFailure(exception); }
            });
        }
        catch (Exception exception) { ReportReadFailure(exception); return false; }
    }

    private void Publish(EndpointNotification notification)
    {
        if (!stopped) onNotification?.Invoke(notification);
    }

    private void ReportReadFailure(Exception exception, string? instanceId = null)
    {
        LogCallbackFailure(exception);
        if (stopped) return;
        var failure = new EndpointReadFailure(exception.Message, exception.HResult, instanceId);
        try
        {
            // 错误也是托管信号；直接投递，避免 Post/Report 互相递归。
            tryPost(() =>
            {
                if (stopped) return;
                try { onNotification?.Invoke(failure); }
                catch (Exception deliveryError) { LogCallbackFailure(deliveryError); }
            });
        }
        catch (Exception deliveryError) { LogCallbackFailure(deliveryError); }
    }

    private void LogCallbackFailure(Exception exception)
    {
        // 日志 provider 的异常也不能越过本机回调边界。
        try { logger.LogWarning(exception, "设备 {DeviceId} 音频事件处理失败", deviceId); }
        catch { }
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        if (endpointVolume is not null) endpointVolume.OnVolumeNotification -= OnMasterVolume;
        foreach (var subscription in subscriptions.Values.ToArray())
        {
            try { Remove(subscription, notify: false); }
            catch (Exception exception) { logger.LogWarning(exception, "释放会话 {InstanceId} 失败，继续清理其余会话", subscription.Id); }
        }
        try
        {
            if (manager is not null && sessionNotification is not null)
                SessionComInterop.Check(manager.Interface.UnregisterSessionNotification(sessionNotification));
        }
        catch (Exception exception) { logger.LogWarning(exception, "解绑设备 {DeviceId} 创建通知失败", deviceId); }
        finally
        {
            try { manager?.Dispose(); }
            finally { try { device?.Dispose(); } finally { enumerator?.Dispose(); } }
        }
    }

    private sealed class Subscription(NativeSession session, string id)
    {
        internal NativeSession Session { get; } = session;
        internal string Id { get; } = id;
        internal SessionEvents? Callback { get; set; }
        internal bool Registered { get; set; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SessionNotification(NAudioEndpointSource owner) : INativeSessionNotification
    {
        public int OnSessionCreated(IntPtr session)
        {
            try { owner.OnCreated(session); }
            catch (Exception exception) { owner.ReportReadFailure(exception); }
            return 0;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SessionEvents(NAudioEndpointSource owner, Subscription subscription) : INativeSessionEvents
    {
        public int OnDisplayNameChanged(string name, IntPtr context) => 0;
        public int OnIconPathChanged(string path, IntPtr context) => 0;
        public int OnChannelVolumeChanged(uint channels, IntPtr volumes, uint changedChannel, IntPtr context) => 0;
        public int OnGroupingParamChanged(IntPtr grouping, IntPtr context) => 0;
        public int OnSimpleVolumeChanged(float volume, bool mute, IntPtr context)
            => Guard(() => owner.OnVolume(subscription, volume, mute, SessionComInterop.ReadContext(context)));
        public int OnStateChanged(int state)
            => Guard(() => owner.OnState(subscription, (AudioSessionState)state));
        public int OnSessionDisconnected(int reason) => Guard(() => owner.OnDisconnected(subscription, reason));
        private int Guard(Action action)
        {
            try { action(); }
            catch (Exception exception) { owner.ReportReadFailure(exception, subscription.Id); }
            return 0;
        }
    }
}
