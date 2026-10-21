using System.Collections.ObjectModel;
using AudioManager.Core.Models;
using AudioManager.Windows.AudioSessions;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace AudioManager.Windows.Notifications;

/// <summary>订阅后读取 Render 设备，避免把设备枚举工作放入系统通知线程。</summary>
internal sealed class NAudioDeviceSource(ILogger logger) : IAudioDeviceSource
{
    private MMDeviceEnumerator? enumerator;
    private MMDeviceNotificationClient? client;
    private Action<DeviceNotification>? onChanged;
    private volatile bool stopped;

    public DeviceInitialState Initialize(Action<DeviceNotification> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        ObjectDisposedException.ThrowIf(stopped, this);
        if (enumerator is not null) throw new InvalidOperationException("设备通知源已经初始化。");
        this.onChanged = onChanged;
        try
        {
            enumerator = new MMDeviceEnumerator();
            // 不捕获 UI 上下文；所有事件处理仅复制参数并投递工作，不在回调中查询设备。
            client = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            client.DeviceAdded += OnDeviceAdded;
            client.DeviceRemoved += OnDeviceRemoved;
            client.DeviceStateChanged += OnDeviceStateChanged;
            client.DefaultDeviceChanged += OnDefaultDeviceChanged;
            client.PropertyValueChanged += OnPropertyValueChanged;
            return ReadInitialState(enumerator);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private DeviceInitialState ReadInitialState(MMDeviceEnumerator deviceEnumerator)
    {
        var devices = new List<AudioOutputDevice>();
        var issues = new List<AudioReadIssue>();
        using (var endpoints = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
        {
            var count = endpoints.Count;
            for (var index = 0; index < count; index++)
            {
                try
                {
                    using var device = endpoints[index];
                    var id = device.ID;
                    var state = (AudioDeviceState)device.State;
                    string name;
                    try { name = device.FriendlyName; }
                    catch (Exception error) { SessionSnapshotReader.AddIssue(id, error, issues); name = id; }
                    devices.Add(new(id, string.IsNullOrWhiteSpace(name) ? id : name, state));
                }
                catch (Exception error) { SessionSnapshotReader.AddIssue($"设备索引 {index}", error, issues); }
            }
        }
        var defaults = new Dictionary<AudioDeviceRole, string?>();
        foreach (var role in Enum.GetValues<AudioDeviceRole>())
        {
            defaults[role] = null;
            try
            {
                if (!deviceEnumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, (Role)role, out var device)) continue;
                using (device)
                {
                    if (device?.State == DeviceState.Active) defaults[role] = device.ID;
                }
            }
            catch (Exception error) { SessionSnapshotReader.AddIssue($"默认设备 {role}", error, issues); }
        }
        foreach (var issue in issues)
            logger.LogWarning("设备状态部分读取失败：{ItemId}，{Message}，HRESULT={HResult}",
                issue.ItemId, issue.Message, issue.NativeErrorCode);
        return new(new(devices.AsReadOnly(), issues.AsReadOnly()),
            new ReadOnlyDictionary<AudioDeviceRole, string?>(defaults));
    }

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs args)
        => Forward(new(DeviceNotificationKind.Added, args.DeviceId));

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs args)
        => Forward(new(DeviceNotificationKind.Removed, args.DeviceId));

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs args)
        => Forward(new(DeviceNotificationKind.StateChanged, args.DeviceId, (AudioDeviceState)args.NewState));

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs args)
    {
        if (args.Flow == DataFlow.Render)
            Forward(new(DeviceNotificationKind.DefaultChanged, args.DeviceId, Role: (AudioDeviceRole)args.Role));
    }

    private void OnPropertyValueChanged(object? sender, DevicePropertyChangedEventArgs args)
        => Forward(new(DeviceNotificationKind.PropertyChanged, args.DeviceId));

    private void Forward(DeviceNotification notification)
    {
        if (stopped) return;
        try { onChanged?.Invoke(notification); }
        catch (Exception error) { logger.LogWarning(error, "投递设备通知失败：{Kind}，{DeviceId}", notification.Kind, notification.DeviceId); }
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        if (client is not null)
        {
            client.DeviceAdded -= OnDeviceAdded;
            client.DeviceRemoved -= OnDeviceRemoved;
            client.DeviceStateChanged -= OnDeviceStateChanged;
            client.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            client.PropertyValueChanged -= OnPropertyValueChanged;
        }
        // 只在拥有该源的 MTA 队列执行；绝不在 Windows 回调里反注册。
        try { client?.Dispose(); }
        finally { enumerator?.Dispose(); }
    }
}
