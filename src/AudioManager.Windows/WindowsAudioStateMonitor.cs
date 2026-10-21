using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Core.Services;
using AudioManager.Windows.AudioSessions;
using AudioManager.Windows.Interop;
using AudioManager.Windows.Notifications;
using Microsoft.Extensions.Logging;

namespace AudioManager.Windows;

/// <summary>系统事件驱动的实时状态入口；不执行音量写入，也不接触用户保存的配置。</summary>
public sealed class WindowsAudioStateMonitor : IAudioStateMonitor
{
    private readonly Func<IAudioDeviceSource> deviceSourceFactory;
    private readonly Func<string, Func<Action, bool>, IAudioEndpointSource> endpointSourceFactory;
    private readonly Func<DeviceInitialState> readDevices;
    private readonly ILogger<WindowsAudioStateMonitor> logger;
    private readonly AudioThread audioThread;
    private readonly TimeSpan[] recoveryDelays;
    private readonly AudioStateStore state = new();
    private readonly object gate = new();
    private readonly Dictionary<string, Registration> endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recovery> recoveries = new(StringComparer.Ordinal);
    private readonly Channel<AudioStateChanged> changes = Channel.CreateUnbounded<AudioStateChanged>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private IAudioDeviceSource? deviceSource;
    private object? runToken;
    private bool disposed;
    private int reading;

    public WindowsAudioStateMonitor(ILogger<WindowsAudioStateMonitor> logger)
        : this(() => new NAudioDeviceSource(logger), (id, post) => new NAudioEndpointSource(id, logger, post),
            () => ReadDevices(new NAudioBackend(logger)), logger, new AudioThread()) { }

    internal WindowsAudioStateMonitor(Func<IAudioDeviceSource> deviceSourceFactory,
        Func<string, Func<Action, bool>, IAudioEndpointSource> endpointSourceFactory,
        Func<DeviceInitialState> readDevices, ILogger<WindowsAudioStateMonitor> logger, AudioThread audioThread,
        TimeSpan[]? recoveryDelays = null)
    {
        this.deviceSourceFactory = deviceSourceFactory;
        this.endpointSourceFactory = endpointSourceFactory;
        this.readDevices = readDevices;
        this.logger = logger;
        this.audioThread = audioThread;
        this.recoveryDelays = (recoveryDelays ?? [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)]).ToArray();
    }

    public Task<AudioRuntimeState> StartAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("启动实时音频监听", () =>
        {
            if (runToken is not null) return state.Snapshot();
            var token = new object();
            runToken = token;
            try
            {
                deviceSource = deviceSourceFactory();
                var initial = deviceSource.Initialize(notification =>
                {
                    // 端点音量也可能触发 property 通知；不因此全量重新枚举或重建监听。
                    if (notification.Kind != DeviceNotificationKind.PropertyChanged)
                        TryPost(() => { if (ReferenceEquals(runToken, token)) HandleDevice(notification); });
                });
                state.ReplaceDevices(initial.Devices, initial.Defaults);
                state.SetRunning(true);
                ReconcileEndpoints(AudioChangeOrigin.Initialization);
                Publish(AudioStateChangeKind.Started, AudioChangeOrigin.Initialization);
                return state.Snapshot();
            }
            catch { StopCore(publish: false); throw; }
        }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => Schedule(() => { StopCore(publish: true); return true; }, cancellationToken);

    public Task<AudioRuntimeState> GetStateAsync(CancellationToken cancellationToken = default)
        => Schedule(state.Snapshot, cancellationToken);

    public Task<AudioRuntimeState> RefreshAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("刷新实时音频状态", () =>
        {
            if (runToken is null) throw new InvalidOperationException("实时监听尚未启动。");
            CancelRecoveries();
            RefreshDevices(AudioChangeOrigin.Recovery);
            return state.Snapshot();
        }, cancellationToken);

    public async IAsyncEnumerable<AudioStateChanged> ReadChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
            throw new InvalidOperationException("实时状态通知只支持一个同时运行的消费者。");
        try
        {
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return change;
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    private void HandleDevice(DeviceNotification notification)
    {
        if (notification.DeviceId is { } id)
        {
            ResetRecovery(id);
            if (notification.Kind == DeviceNotificationKind.Removed
                || notification.Kind == DeviceNotificationKind.StateChanged && notification.State != AudioDeviceState.Active)
            {
                RemoveEndpoint(id);
                if (state.ClearEndpoint(id)) Publish(AudioStateChangeKind.DeviceListeningChanged, AudioChangeOrigin.System, id);
            }
        }
        try { RefreshDevices(AudioChangeOrigin.System, notification); }
        catch (Exception exception)
        {
            logger.LogError(exception, "设备通知后重新枚举失败");
            Publish(AudioStateChangeKind.ReadFailure, AudioChangeOrigin.System, notification.DeviceId, message: exception.Message);
            // 事件已明确设备失效时，不能因一次枚举失败仍保留旧的有效状态。
            if (notification.Kind is DeviceNotificationKind.Removed or DeviceNotificationKind.StateChanged or DeviceNotificationKind.DefaultChanged)
            {
                var previous = state.Snapshot();
                var fallback = new DeviceInitialState(new(previous.Devices.Select(item => item.Device).ToArray(), previous.Issues), previous.DefaultOutputDevices);
                ApplyDevices(WithNotification(fallback, notification), AudioChangeOrigin.System, notification);
            }
            if (notification.DeviceId is { } failedId) ScheduleRecovery(failedId);
        }
    }

    private void RefreshDevices(AudioChangeOrigin origin, DeviceNotification? notification = null)
    {
        var current = readDevices();
        ApplyDevices(notification is null ? current : WithNotification(current, notification), origin, notification);
    }

    private void ApplyDevices(DeviceInitialState current, AudioChangeOrigin origin, DeviceNotification? notification)
    {
        if (state.ReplaceDevices(current.Devices, current.Defaults))
            Publish(notification?.Kind == DeviceNotificationKind.DefaultChanged
                ? AudioStateChangeKind.DefaultDeviceChanged : AudioStateChangeKind.DevicesChanged,
                notification is null ? origin : AudioChangeOrigin.External, notification?.DeviceId,
                message: notification?.Role?.ToString());
        ReconcileEndpoints(origin);
    }

    private static DeviceInitialState WithNotification(DeviceInitialState current, DeviceNotification notification)
    {
        var devices = current.Devices.Items;
        if (notification.Kind == DeviceNotificationKind.Removed)
            devices = devices.Where(item => item.Id != notification.DeviceId).ToArray();
        else if (notification.Kind == DeviceNotificationKind.StateChanged && notification.State is { } nextState)
            devices = devices.Select(item => item.Id == notification.DeviceId ? item with { State = nextState } : item).ToArray();
        var defaults = current.Defaults.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (notification.Kind == DeviceNotificationKind.DefaultChanged && notification.Role is { } role)
            defaults[role] = string.IsNullOrWhiteSpace(notification.DeviceId) ? null : notification.DeviceId;
        return new(new(devices, current.Devices.Issues), defaults);
    }

    private void ReconcileEndpoints(AudioChangeOrigin origin)
    {
        var active = state.Snapshot().Devices.Where(item => item.Device.State == AudioDeviceState.Active)
            .Select(item => item.Device.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in endpoints.Keys.Where(id => !active.Contains(id)).ToArray()) RemoveEndpoint(id);
        foreach (var id in recoveries.Keys.Where(id => !active.Contains(id)).ToArray()) ResetRecovery(id);
        foreach (var id in active)
        {
            if (endpoints.ContainsKey(id)) continue;
            // 其他设备的事件不能绕过此设备的等待时间或已经耗尽的恢复预算。
            if (recoveries.TryGetValue(id, out var pending) && !pending.Ready) continue;
            if (pending is not null) pending.Ready = false;
            Registration? registration = null;
            try
            {
                registration = new(endpointSourceFactory(id, TryPost));
                endpoints.Add(id, registration);
                var captured = registration;
                var initial = registration.Source.Initialize(notification =>
                    TryPost(() => { if (IsCurrent(id, captured)) HandleEndpoint(id, notification); }));
                if (state.SetEndpoint(id, initial.MasterVolume, initial.Sessions))
                    Publish(AudioStateChangeKind.DeviceListeningChanged, origin, id);
                MarkRecoveryStable(id);
            }
            catch (Exception exception)
            {
                endpoints.Remove(id);
                if (registration is not null) Release(registration.Source);
                logger.LogWarning(exception, "初始化设备 {DeviceId} 实时监听失败", id);
                state.ClearEndpoint(id, new(id, exception.Message, exception.HResult));
                Publish(AudioStateChangeKind.ReadFailure, origin, id, message: exception.Message);
                ScheduleRecovery(id);
            }
        }
    }

    private void HandleEndpoint(string deviceId, EndpointNotification notification)
    {
        switch (notification)
        {
            case EndpointMasterVolumeChanged master:
                if (state.SetMasterVolume(deviceId, master.Volume))
                    Publish(AudioStateChangeKind.MasterVolumeChanged, Origin(master.EventContext), deviceId, context: master.EventContext);
                break;
            case EndpointSessionCreated created:
                var issues = created.Data.Issues.ToList();
                var session = SessionSnapshotReader.Read(deviceId, created.Data.Info, issues);
                foreach (var issue in issues) logger.LogWarning("新增会话信息部分不可读：{ItemId} {Message}", issue.ItemId, issue.Message);
                if (state.UpsertSession(session))
                    Publish(AudioStateChangeKind.SessionCreated, AudioChangeOrigin.System, deviceId, session.Key,
                        message: issues.Count == 0 ? null : string.Join("；", issues.Select(issue => issue.Message)));
                break;
            case EndpointSessionVolumeChanged volume:
                var volumeKey = new AudioSessionKey(deviceId, volume.InstanceId);
                if (state.SetSessionVolume(volumeKey, volume.Volume))
                    Publish(AudioStateChangeKind.SessionVolumeChanged, Origin(volume.EventContext), deviceId, volumeKey, volume.EventContext);
                break;
            case EndpointSessionStateChanged activity:
                var stateKey = new AudioSessionKey(deviceId, activity.InstanceId);
                if (state.SetSessionState(stateKey, activity.State))
                    Publish(activity.State == AudioSessionState.Expired ? AudioStateChangeKind.SessionRemoved : AudioStateChangeKind.SessionStateChanged,
                        AudioChangeOrigin.System, deviceId, stateKey);
                break;
            case EndpointSessionRemoved removed:
                var removedKey = new AudioSessionKey(deviceId, removed.InstanceId);
                if (state.RemoveSession(removedKey)) Publish(AudioStateChangeKind.SessionRemoved, AudioChangeOrigin.System, deviceId, removedKey);
                break;
            case EndpointReadFailure failure:
                var failedKey = string.IsNullOrWhiteSpace(failure.InstanceId) ? null : new AudioSessionKey(deviceId, failure.InstanceId);
                Publish(AudioStateChangeKind.ReadFailure, AudioChangeOrigin.System, deviceId, failedKey,
                    message: failure.Reason, issue: new(failure.InstanceId ?? deviceId, failure.Reason, failure.NativeErrorCode ?? 0));
                break;
            case EndpointInvalidated invalidated:
                RemoveEndpoint(deviceId);
                state.ClearEndpoint(deviceId, new(deviceId, invalidated.Reason, invalidated.NativeErrorCode ?? 0));
                Publish(AudioStateChangeKind.ReadFailure, AudioChangeOrigin.System, deviceId, message: invalidated.Reason);
                ScheduleRecovery(deviceId);
                break;
        }
    }

    private static AudioChangeOrigin Origin(Guid context) => context == AudioOperationContext.Id ? AudioChangeOrigin.Self : AudioChangeOrigin.External;

    private bool IsCurrent(string id, Registration registration) => runToken is not null
        && endpoints.TryGetValue(id, out var current) && ReferenceEquals(current, registration);

    private void ScheduleRecovery(string deviceId)
    {
        if (runToken is null) return;
        if (!recoveries.TryGetValue(deviceId, out var recovery))
            recoveries.Add(deviceId, recovery = new Recovery(runToken));
        recovery.StableTimer?.Dispose();
        recovery.StableTimer = null;
        if (recovery.Timer is not null || recovery.Attempts >= recoveryDelays.Length) return;
        var delay = recoveryDelays[recovery.Attempts++];
        recovery.Timer = new Timer(_ => TryPost(() =>
        {
            if (!ReferenceEquals(runToken, recovery.Token) || !recoveries.TryGetValue(deviceId, out var current)
                || !ReferenceEquals(current, recovery)) return;
            recovery.Timer?.Dispose();
            recovery.Timer = null;
            recovery.Ready = true;
            try { RefreshDevices(AudioChangeOrigin.Recovery); }
            catch (Exception exception)
            {
                recovery.Ready = false;
                logger.LogWarning(exception, "设备 {DeviceId} 监听恢复失败", deviceId);
                Publish(AudioStateChangeKind.ReadFailure, AudioChangeOrigin.Recovery, deviceId, message: exception.Message);
                ScheduleRecovery(deviceId);
            }
        }), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void ResetRecovery(string id)
    {
        if (recoveries.Remove(id, out var recovery))
        {
            recovery.Timer?.Dispose();
            recovery.StableTimer?.Dispose();
        }
    }

    private void MarkRecoveryStable(string id)
    {
        if (!recoveries.TryGetValue(id, out var recovery)) return;
        recovery.Timer?.Dispose();
        recovery.Timer = null;
        recovery.StableTimer?.Dispose();
        // 短时间内反复初始化成功又失效属于同一故障，不能每次清空重试预算。
        recovery.StableTimer = new Timer(_ => TryPost(() =>
        {
            if (ReferenceEquals(runToken, recovery.Token) && recoveries.TryGetValue(id, out var current)
                && ReferenceEquals(current, recovery) && endpoints.ContainsKey(id)) ResetRecovery(id);
        }), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private void CancelRecoveries()
    {
        foreach (var recovery in recoveries.Values)
        {
            recovery.Timer?.Dispose();
            recovery.StableTimer?.Dispose();
        }
        recoveries.Clear();
    }

    private void RemoveEndpoint(string id)
    {
        if (endpoints.Remove(id, out var registration)) Release(registration.Source);
    }

    private void Release(IDisposable value)
    {
        try { value.Dispose(); }
        catch (Exception exception) { logger.LogError(exception, "释放实时音频监听资源失败"); }
    }

    private void StopCore(bool publish)
    {
        if (runToken is null) return;
        runToken = null;
        CancelRecoveries();
        foreach (var id in endpoints.Keys.ToArray())
        {
            RemoveEndpoint(id);
            state.ClearEndpoint(id);
        }
        if (deviceSource is not null) Release(deviceSource);
        deviceSource = null;
        state.SetRunning(false);
        if (publish) Publish(AudioStateChangeKind.Stopped, AudioChangeOrigin.System);
    }

    private void Publish(AudioStateChangeKind kind, AudioChangeOrigin origin, string? deviceId = null,
        AudioSessionKey? sessionKey = null, Guid? context = null, string? message = null, AudioReadIssue? issue = null)
        => changes.Writer.TryWrite(new(kind, origin, state.Snapshot(), deviceId, sessionKey, context, message, issue));

    private bool TryPost(Action action)
    {
        Task<bool> task;
        lock (gate)
        {
            if (disposed) return false;
            task = audioThread.InvokeAsync(() =>
            {
                try { action(); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "处理音频事件失败");
                    Publish(AudioStateChangeKind.ReadFailure, AudioChangeOrigin.System, message: exception.Message);
                }
                return true;
            }, default);
        }
        _ = ObserveAsync(task);
        return true;
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) { logger.LogError(exception, "音频事件队列失败"); }
    }

    private Task<T> Schedule<T>(Func<T> action, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return audioThread.InvokeAsync(action, cancellationToken);
        }
    }

    private async Task<T> ExecuteAsync<T>(string operation, Func<T> action, CancellationToken cancellationToken)
    {
        try { return await Schedule(action, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException and not ObjectDisposedException)
        {
            logger.LogError(exception, "音频状态操作失败：{Operation}", operation);
            if (exception is AudioControlException) throw;
            var code = exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005)
                ? AudioErrorCode.AccessDenied : AudioErrorCode.ApiFailure;
            throw new AudioControlException(code, operation, $"{operation}失败。", exception);
        }
    }

    private static DeviceInitialState ReadDevices(IAudioBackend backend)
    {
        var devices = backend.EnumerateOutputDevices();
        var issues = devices.Issues.ToList();
        var defaults = new Dictionary<AudioDeviceRole, string?>();
        foreach (var role in Enum.GetValues<AudioDeviceRole>())
        {
            try { defaults[role] = backend.GetDefaultOutputDevice(role)?.Id; }
            catch (Exception exception)
            {
                defaults[role] = null;
                SessionSnapshotReader.AddIssue($"默认设备 {role}", exception, issues);
            }
        }
        return new(new(devices.Items, issues.AsReadOnly()), defaults);
    }

    public void Dispose()
    {
        Task<bool> cleanup;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cleanup = audioThread.InvokeAsync(() =>
            {
                StopCore(publish: true);
                changes.Writer.TryComplete();
                return true;
            }, default);
        }
        try { cleanup.GetAwaiter().GetResult(); }
        catch (Exception exception) { logger.LogError(exception, "关闭实时音频线程失败"); }
        finally { changes.Writer.TryComplete(); audioThread.Dispose(); }
    }

    private sealed record Registration(IAudioEndpointSource Source);
    private sealed class Recovery(object token)
    {
        internal object Token { get; } = token;
        internal int Attempts { get; set; }
        internal bool Ready { get; set; }
        internal Timer? Timer { get; set; }
        internal Timer? StableTimer { get; set; }
    }
}
