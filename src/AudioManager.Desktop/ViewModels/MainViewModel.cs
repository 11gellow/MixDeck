using System.Collections.ObjectModel;
using System.Windows.Input;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Core.Services;
using AudioManager.Desktop.Services;

namespace AudioManager.Desktop.ViewModels;

public enum SyncState { Starting, Live, Degraded, Offline }

/// <summary>
/// 主窗口状态。读取全部来自 <see cref="IAudioStateMonitor"/> 的实时快照与变化通知（不轮询），
/// 写入经 <see cref="IAudioController"/>；界面只展示实时状态，不保存或推导目标音量。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAudioController controller;
    private readonly IAudioStateMonitor monitor;
    private readonly AppIconProvider icons;
    private readonly TimeProvider time;
    private readonly IDefaultAudioOutputController? defaultOutput;
    private readonly LatestValueWriter<(string DeviceId, float Level)> masterWriter;
    private readonly SelfEchoGuard masterEchoGuard;
    private readonly CancellationTokenSource lifetime = new();
    private AudioRuntimeState? runtimeState;
    private readonly Dictionary<object, (Task<Guid> Token, Func<Task> Drain)> volumeEdits = [];
    private Task? masterEditStarted;
    private string? defaultDeviceId;
    private string? loadedDeviceId;
    private string? deviceListSignature;
    private bool isSwitchingOutput;
    private bool suppressSelection;
    private bool isConsuming;
    private bool disposed;
    private DeviceItemViewModel? selectedDevice;
    private bool showUnavailableDevices;
    private bool isLoading;
    private bool hasMasterVolume;
    private double masterVolume;
    private bool isMasterMuted;
    private bool isMasterMuteChanging;
    private string? masterExternalChangeText;
    private string deviceStatusText = "正在启动实时监听…";
    private bool isDeviceWarning;
    private IReadOnlyList<AudioReadIssue> readIssues = [];
    private SyncState syncState = SyncState.Starting;
    private string syncStatusText = "正在启动实时监听…";
    private string stateVersionText = "";

    public MainViewModel(IAudioController controller, IAudioStateMonitor monitor, AppIconProvider icons, OperationLog log, TimeProvider time,
        ConfigurationViewModel? configuration = null, IDefaultAudioOutputController? defaultOutput = null)
    {
        this.controller = controller;
        this.monitor = monitor;
        this.icons = icons;
        this.time = time;
        this.defaultOutput = defaultOutput;
        Log = log;
        Configuration = configuration;
        if (Configuration is not null) Configuration.BeforeApply = FinishVolumeEditsAsync;
        if (Configuration is not null)
        {
            Configuration.Service.Changed += OnConfigurationChanged;
            Configuration.Service.RestoreReadback += OnRestoreReadback;
        }
        if (controller is AudioWriteGuard guard) guard.Changed += UpdateControlAccess;
        masterEchoGuard = new SelfEchoGuard(time);
        masterWriter = new LatestValueWriter<(string, float)>(WriteMasterVolumeAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && !IsSwitchingOutput);
        ToggleMasterMuteCommand = new AsyncRelayCommand(ToggleMasterMuteAsync, () => CanControlMaster && !isMasterMuteChanging);
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptySessions));
    }

    public OperationLog Log { get; }
    public ConfigurationViewModel? Configuration { get; }

    public void BeginVolumeEdit(SessionViewModel? session = null)
    {
        object key = session ?? (object)this;
        if (volumeEdits.ContainsKey(key) || Configuration is null || loadedDeviceId is null) return;
        var deviceId = session?.Key.DeviceId ?? loadedDeviceId;
        var token = BeginAsync();
        volumeEdits.Add(key, (token, session is null ? masterWriter.WhenIdleAsync : session.WaitForVolumeAsync));
        if (session is null) masterEditStarted = token;
        else session.VolumeEditStarted = token;
        async Task<Guid> BeginAsync()
        {
            try { return await Configuration.Service.BeginVolumeEditAsync(deviceId, session?.Key); }
            catch (Exception exception) { Log.Error("无法延迟保存音量编辑；系统音量仍可调节", exception); return Guid.Empty; }
        }
    }

    private readonly SemaphoreSlim finishEdits = new(1, 1);
    public async Task FinishVolumeEditsAsync()
    {
        await finishEdits.WaitAsync();
        try
        {
            foreach (var (key, edit) in volumeEdits.ToArray())
            {
                try
                {
                    var token = await edit.Token;
                    await edit.Drain();
                    await Configuration!.Service.EndVolumeEditAsync(token);
                }
                catch (Exception exception) { Log.Error("完成音量编辑失败", exception); }
                finally
                {
                    volumeEdits.Remove(key);
                    if (key is SessionViewModel session) session.VolumeEditStarted = null;
                    else masterEditStarted = null;
                }
            }
        }
        finally { finishEdits.Release(); }
    }

    public async Task FlushEditsAsync()
    {
        await FinishVolumeEditsAsync();
        await masterWriter.WhenIdleAsync();
        foreach (var session in Sessions.ToArray()) await session.WaitForVolumeAsync();
        if (Configuration is not null) await Configuration.CommitInputsAsync();
    }
    public bool CanControlMaster => HasMasterVolume && !IsSwitchingOutput && MasterControlStatus is null;
    public bool IsSwitchingOutput
    {
        get => isSwitchingOutput;
        private set
        {
            if (!SetProperty(ref isSwitchingOutput, value)) return;
            OnPropertyChanged(nameof(CanSelectOutput));
            OnPropertyChanged(nameof(CanControlMaster));
            UpdateControlAccess();
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool CanSelectOutput => !IsLoading && !IsSwitchingOutput;
    internal Task OutputSwitchTask { get; private set; } = Task.CompletedTask;
    private string? masterControlStatus;
    public string? MasterControlStatus { get => masterControlStatus; private set { SetProperty(ref masterControlStatus, value); OnPropertyChanged(nameof(CanControlMaster)); CommandManager.InvalidateRequerySuggested(); } }
    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public ICommand ToggleMasterMuteCommand { get; }

    public DeviceItemViewModel? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            // 状态回传重建列表不能反向写系统；只有用户选择另一个可用端点才发起切换。
            if (suppressSelection || disposed || value is null || value.Id == selectedDevice?.Id) return;
            if (IsSwitchingOutput) { OnPropertyChanged(); return; }
            OutputSwitchTask = SwitchOutputAsync(value);
        }
    }

    public bool ShowUnavailableDevices
    {
        get => showUnavailableDevices;
        set
        {
            if (!SetProperty(ref showUnavailableDevices, value)) return;
            RebuildDeviceList(SelectedDevice?.Id);
            ApplySelectedDevice();
            UpdateControlAccess();
            if (Configuration is not null && Configuration.Service.Current.Settings.ShowUnavailableDevices != value)
                Configuration.ShowUnavailableDevices = value;
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (SetProperty(ref isLoading, value))
            {
                OnPropertyChanged(nameof(CanSelectOutput));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasMasterVolume
    {
        get => hasMasterVolume;
        private set
        {
            if (SetProperty(ref hasMasterVolume, value))
            {
                OnPropertyChanged(nameof(ShowEmptySessions));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>界面主音量 0～100，对应 API 标量 0～1。</summary>
    public double MasterVolume
    {
        get => masterVolume;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (!CanControlMaster) return;
            if (SetProperty(ref masterVolume, value) && loadedDeviceId is { } deviceId)
                _ = masterWriter.SubmitAsync((deviceId, (float)(value / 100)));
        }
    }

    public bool IsMasterMuted { get => isMasterMuted; private set => SetProperty(ref isMasterMuted, value); }
    public string? MasterExternalChangeText { get => masterExternalChangeText; private set => SetProperty(ref masterExternalChangeText, value); }
    public string DeviceStatusText { get => deviceStatusText; private set => SetProperty(ref deviceStatusText, value); }
    public bool IsDeviceWarning { get => isDeviceWarning; private set => SetProperty(ref isDeviceWarning, value); }
    public SyncState SyncState { get => syncState; private set => SetProperty(ref syncState, value); }
    public string SyncStatusText { get => syncStatusText; private set => SetProperty(ref syncStatusText, value); }
    public string StateVersionText { get => stateVersionText; private set => SetProperty(ref stateVersionText, value); }

    public bool ShowEmptySessions => HasMasterVolume && Sessions.Count == 0;
    public bool HasReadIssues => readIssues.Count > 0;
    public string ReadIssueSummary => $"{readIssues.Count} 项信息读取不完整";
    public string ReadIssueDetails => string.Join(Environment.NewLine, readIssues.Select(issue => $"{issue.ItemId}：{issue.Message}"));

    public async Task InitializeAsync()
    {
        if (Configuration is not null)
        {
            await Configuration.InitializeAsync();
            ShowUnavailableDevices = Configuration.Service.Current.Settings.ShowUnavailableDevices;
        }
        await RefreshCommand.ExecuteAsync();
    }

    private void OnConfigurationChanged()
    {
        if (Configuration is not null) ShowUnavailableDevices = Configuration.Service.Current.Settings.ShowUnavailableDevices;
        UpdateControlAccess();
    }

    private void UpdateControlAccess()
    {
        MasterControlStatus = loadedDeviceId is { } id
            ? (controller as AudioWriteGuard)?.Restriction(AudioWriteGuard.MasterKey(id)) is { } reason ? "无法修改：" + reason : null
            : null;
        foreach (var session in Sessions)
        {
            var sessionReason = (controller as AudioWriteGuard)?.Restriction(AudioWriteGuard.SessionKey(session.Key));
            session.SetControlStatus(IsSwitchingOutput ? "正在切换输出设备，请稍候" : sessionReason is not null ? "无法修改：" + sessionReason : null);
        }
    }

    private async Task SwitchOutputAsync(DeviceItemViewModel requested)
    {
        if (!requested.IsActive)
        {
            SetDeviceStatus($"该设备当前{requested.StateText}，不能设为系统输出设备", true);
            OnPropertyChanged(nameof(SelectedDevice));
            return;
        }
        if (defaultOutput is null)
        {
            SetDeviceStatus("当前无法切换系统输出设备，请重新启动程序后重试", true);
            Log.Warning(DeviceStatusText, "本软件");
            OnPropertyChanged(nameof(SelectedDevice));
            return;
        }
        IsSwitchingOutput = true;
        SetDeviceStatus($"正在切换系统输出设备：{requested.Name}…", false);
        try
        {
            await FlushEditsAsync();
            await defaultOutput.SetDefaultOutputDeviceAsync(requested.Id, lifetime.Token);
            var state = await monitor.RefreshAsync(lifetime.Token);
            ApplyState(state);
            if (!string.Equals(state.DefaultOutputDevices.GetValueOrDefault(AudioDeviceRole.Multimedia), requested.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("系统回读的默认输出与所选设备不同，未确认切换成功。");
            if (Configuration is not null) await Configuration.Service.HandleStateAsync(state);
            Log.Info($"系统默认输出已切换为：{requested.Name}", "本软件");
        }
        catch (Exception exception) when (exception is OperationCanceledException || disposed) { }
        catch (Exception exception)
        {
            Log.Error($"切换输出设备“{requested.Name}”失败", exception);
            try
            {
                var state = await monitor.RefreshAsync(lifetime.Token);
                ApplyState(state);
                if (Configuration is not null) await Configuration.Service.HandleStateAsync(state);
            }
            catch (Exception refreshException) when (refreshException is not OperationCanceledException)
            { Log.Error("读取切换后的实际输出设备失败，请点击刷新重试", refreshException); }
            SetDeviceStatus($"切换失败：{exception.Message} 当前显示设备：{SelectedDevice?.Name ?? "无"}", true);
        }
        finally
        {
            IsSwitchingOutput = false;
            OnPropertyChanged(nameof(SelectedDevice));
        }
    }

    private void OnRestoreReadback(string deviceId, AudioSessionKey? sessionKey, AudioVolume actual)
    {
        if (loadedDeviceId != deviceId) return;
        if (sessionKey is null)
        {
            masterEchoGuard.Wrote(actual.Level);
            ApplyMasterVolume(actual, force: true);
        }
        else Sessions.FirstOrDefault(session => session.Key == sessionKey)?.AcceptRestoreReadback(actual);
    }

    /// <summary>未运行时启动实时监听；运行中则重新枚举设备并重试失效监听。</summary>
    private async Task RefreshAsync()
    {
        if (disposed) return;
        IsLoading = true;
        var wasRunning = runtimeState is { IsRunning: true };
        try
        {
            await FlushEditsAsync();
            (controller as AudioWriteGuard)?.ClearRestrictions();
            if (!wasRunning)
            {
                SetSync(SyncState.Starting, "正在启动实时监听…");
                var state = await monitor.StartAsync(lifetime.Token);
                StartConsuming();
                ApplyState(state);
                if (Configuration is not null) await Configuration.Service.HandleStateAsync(state, retry: true);
                var active = state.Devices.Count(device => device.Device.State.HasFlag(AudioDeviceState.Active));
                Log.Info($"实时监听已启动：{state.Devices.Count} 个输出端点（{active} 个可用），当前设备 {Sessions.Count} 个应用会话", "系统");
            }
            else
            {
                var state = await monitor.RefreshAsync(lifetime.Token);
                ApplyState(state);
                if (Configuration is not null) await Configuration.Service.HandleStateAsync(state, retry: true);
                Log.Info("已重新枚举设备并重试失效的监听", "本软件");
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException || disposed) { }
        catch (Exception exception)
        {
            Log.Error(wasRunning ? "刷新实时状态失败" : "启动实时监听失败", exception);
            if (!wasRunning)
            {
                ClearDeviceState();
                SetDeviceStatus("无法读取音频状态，请稍后点击“刷新”重试", true);
            }
            UpdateSyncState();
        }
        finally { IsLoading = false; }
    }

    private void StartConsuming()
    {
        if (isConsuming) return;
        isConsuming = true;
        _ = ConsumeChangesAsync();
    }

    /// <summary>在启动它的线程上下文（UI 线程）中逐条应用通知；监听线程不会直接触碰界面对象。</summary>
    private async Task ConsumeChangesAsync()
    {
        try
        {
            await foreach (var change in monitor.ReadChangesAsync(lifetime.Token))
            {
                if (disposed) break;
                try
                {
                    ApplyChange(change);
                    if (Configuration is not null) await Configuration.Service.HandleStateAsync(change.State, change);
                }
                catch (Exception exception) { Log.Error("更新界面状态失败", exception); }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception exception)
        {
            if (!disposed) Log.Error("实时状态通知中断，点击“刷新”可重新启动", exception);
        }
        finally { isConsuming = false; }
    }

    internal void ApplyChange(AudioStateChanged change)
    {
        var removedName = change is { Kind: AudioStateChangeKind.SessionRemoved, SessionKey: { } removedKey } ? FindSessionName(removedKey) : null;
        var isExternal = change.Origin == AudioChangeOrigin.External;
        ApplyState(change.State,
            isExternal && change.Kind == AudioStateChangeKind.SessionVolumeChanged ? change.SessionKey : null,
            isExternal && change.Kind == AudioStateChangeKind.MasterVolumeChanged ? change.DeviceId : null);
        Describe(change, removedName);
    }

    private void ApplyState(AudioRuntimeState state, AudioSessionKey? externalSession = null, string? externalMasterDevice = null)
    {
        if (runtimeState is { } current && state.Revision < current.Revision) return;
        runtimeState = state;
        defaultDeviceId = state.DefaultOutputDevices.GetValueOrDefault(AudioDeviceRole.Multimedia);
        var preferred = defaultDeviceId ?? SelectedDevice?.Id;
        RebuildDeviceList(preferred);
        ApplySelectedDevice(externalSession, externalMasterDevice);
        StateVersionText = $"状态版本 #{state.Revision} · {state.CapturedAt.ToLocalTime():HH:mm:ss}";
        UpdateSyncState();
        UpdateControlAccess();
    }

    /// <summary>仅在设备集合、名称、状态或默认标记变化时重建下拉列表，避免每次音量事件都打断用户操作。</summary>
    private void RebuildDeviceList(string? preferredId)
    {
        var items = (runtimeState?.Devices ?? [])
            .Select(entry => entry.Device)
            .Where(device => ShowUnavailableDevices || device.State.HasFlag(AudioDeviceState.Active))
            .Select(device => new DeviceItemViewModel(device, device.Id == defaultDeviceId))
            .OrderByDescending(device => device.IsDefault)
            .ThenByDescending(device => device.IsActive)
            .ThenBy(device => device.Name, StringComparer.CurrentCulture)
            .ToList();

        var signature = string.Join("\n", items.Select(item => $"{item.Id}|{item.Name}|{item.State}|{item.IsDefault}"));
        if (signature != deviceListSignature)
        {
            deviceListSignature = signature;
            suppressSelection = true;
            try
            {
                Devices.Clear();
                foreach (var item in items) Devices.Add(item);
            }
            finally { suppressSelection = false; }
        }

        var target = Devices.FirstOrDefault(device => device.Id == preferredId)
            ?? Devices.FirstOrDefault(device => device.Id == defaultDeviceId)
            ?? Devices.FirstOrDefault(device => device.IsActive)
            ?? Devices.FirstOrDefault();
        selectedDevice = target;
        OnPropertyChanged(nameof(SelectedDevice));
    }

    private void ApplySelectedDevice(AudioSessionKey? externalSession = null, string? externalMasterDevice = null)
    {
        var device = SelectedDevice;
        Configuration?.SetDevice(device?.Id, device?.Name);
        var entry = device is null ? null : runtimeState?.Devices.FirstOrDefault(item => item.Device.Id == device.Id);
        SetReadIssues((runtimeState?.Issues ?? []).Concat(entry?.Issues ?? []).ToList());

        if (device is null)
        {
            ClearDeviceState();
            SetDeviceStatus(runtimeState is null || runtimeState.Devices.Count == 0
                ? "未找到输出设备" : "没有可用的输出设备，可勾选“显示不可用端点”查看全部", true);
            return;
        }

        if (!device.IsActive)
        {
            ClearDeviceState();
            SetDeviceStatus($"该设备当前{device.StateText}，无法读取或控制音量；接入后会自动更新", true);
            return;
        }

        if (entry is not { IsListening: true, MasterVolume: { } master })
        {
            ClearDeviceState();
            SetDeviceStatus(runtimeState is { IsRunning: true }
                ? "该设备的实时监听暂不可用，正在自动恢复；也可点击“刷新”重试"
                : "实时监听未运行，点击“刷新”启动", true);
            return;
        }

        var switched = loadedDeviceId != device.Id;
        if (switched) ClearDeviceState();
        loadedDeviceId = device.Id;
        ApplyMasterVolume(master, force: switched, isExternal: externalMasterDevice == device.Id);
        HasMasterVolume = true;
        MergeSessions(entry.Sessions, externalSession);
        SetDeviceStatus(device.IsDefault ? "Windows 默认输出设备（多媒体）" : "非默认输出设备", false);
    }

    private void MergeSessions(IReadOnlyList<AudioSession> snapshot, AudioSessionKey? externalSession)
    {
        var existing = Sessions.ToDictionary(session => session.Key);
        var ordered = new List<SessionViewModel>();

        foreach (var session in snapshot.Where(session => session.State != AudioSessionState.Expired))
        {
            if (existing.Remove(session.Key, out var viewModel)) viewModel.Update(session, session.Key == externalSession);
            else
            {
                viewModel = new SessionViewModel(session, controller, Log, time);
                viewModel.Icon = icons.GetIcon(session.ProcessPath);
                viewModel.Unavailable += OnSessionUnavailable;
            }
            ordered.Add(viewModel);
        }

        foreach (var removed in existing.Values) removed.Unavailable -= OnSessionUnavailable;

        ordered = ordered
            .OrderBy(session => session.IsSystemSounds)
            .ThenBy(session => session.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(session => session.Key.InstanceId, StringComparer.Ordinal)
            .ToList();

        foreach (var group in ordered.GroupBy(session => session.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var index = 0;
            var members = group.ToList();
            foreach (var member in members) member.SetGroup(++index, members.Count);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Sessions.IndexOf(ordered[i]);
            if (current < 0) Sessions.Insert(i, ordered[i]);
            else if (current != i) Sessions.Move(current, i);
        }
        while (Sessions.Count > ordered.Count) Sessions.RemoveAt(Sessions.Count - 1);
    }

    private void Describe(AudioStateChanged change, string? removedName)
    {
        var source = OriginText(change.Origin);
        var deviceName = DeviceName(change.DeviceId);
        switch (change.Kind)
        {
            case AudioStateChangeKind.Stopped:
                Log.Warning("实时监听已停止，点击“刷新”可重新启动", source);
                break;
            case AudioStateChangeKind.DevicesChanged when change.Origin != AudioChangeOrigin.Initialization:
                var active = change.State.Devices.Count(device => device.Device.State.HasFlag(AudioDeviceState.Active));
                Log.Info($"输出设备列表已更新（{active} 个可用）", source);
                break;
            case AudioStateChangeKind.DefaultDeviceChanged when change.Message is null or nameof(AudioDeviceRole.Multimedia):
                Log.Info($"默认输出设备已切换为：{DeviceName(defaultDeviceId) ?? "无"}", source);
                break;
            case AudioStateChangeKind.DeviceListeningChanged when change.Origin != AudioChangeOrigin.Initialization:
                var listening = change.State.Devices.FirstOrDefault(device => device.Device.Id == change.DeviceId)?.IsListening == true;
                if (listening) Log.Info($"{(change.Origin == AudioChangeOrigin.Recovery ? "已恢复监听" : "已开始监听")}：{deviceName}", source);
                else Log.Warning($"{deviceName}：实时监听已停止（设备不可用或已断开）", source);
                break;
            case AudioStateChangeKind.MasterVolumeChanged when change.Origin == AudioChangeOrigin.External:
                var master = change.State.Devices.FirstOrDefault(device => device.Device.Id == change.DeviceId)?.MasterVolume;
                if (change.DeviceId == loadedDeviceId) MasterExternalChangeText = $"外部修改 {Now:HH:mm:ss}";
                if (master is { } value)
                    Log.Coalesce($"master:{change.DeviceId}", $"系统主音量（{deviceName}）被修改为 {FormatVolume(value)}", source);
                break;
            case AudioStateChangeKind.SessionCreated when change.SessionKey is { } createdKey:
                Log.Info($"新音频会话：{FindSessionName(createdKey) ?? "未知应用"}（{deviceName}）", source);
                break;
            case AudioStateChangeKind.SessionVolumeChanged when change.Origin == AudioChangeOrigin.External && change.SessionKey is { } changedKey:
                Sessions.FirstOrDefault(session => session.Key == changedKey)?.MarkExternalChange(Now);
                var changed = FindSession(changedKey);
                if (changed is not null)
                    Log.Coalesce($"session:{changedKey.DeviceId}|{changedKey.InstanceId}", $"{SessionName(changed)} 被修改为 {FormatVolume(changed.Volume)}", source);
                break;
            case AudioStateChangeKind.SessionRemoved:
                Log.Info($"音频会话已结束：{removedName ?? "未知应用"}", source);
                break;
            case AudioStateChangeKind.ReadFailure:
                Log.Warning($"{(deviceName is null ? "" : deviceName + "：")}{change.Message ?? change.Issue?.Message ?? "读取失败"}", source);
                break;
        }
    }

    private DateTime Now => time.GetLocalNow().DateTime;

    private static string OriginText(AudioChangeOrigin origin) => origin switch
    {
        AudioChangeOrigin.Initialization => "初始化",
        AudioChangeOrigin.External => "外部",
        AudioChangeOrigin.Self => "本软件",
        AudioChangeOrigin.Recovery => "自动恢复",
        _ => "系统"
    };

    private static string FormatVolume(AudioVolume volume) => $"{volume.Level * 100:0}%{(volume.IsMuted ? "（静音）" : "")}";

    private static string SessionName(AudioSession session) => session.IsSystemSounds ? "系统声音" : session.Name;

    private AudioSession? FindSession(AudioSessionKey key)
        => runtimeState?.Devices.FirstOrDefault(device => device.Device.Id == key.DeviceId)?.Sessions.FirstOrDefault(session => session.Key == key);

    private string? FindSessionName(AudioSessionKey key) => FindSession(key) is { } session ? SessionName(session) : null;

    private string? DeviceName(string? deviceId)
        => deviceId is null ? null : runtimeState?.Devices.FirstOrDefault(device => device.Device.Id == deviceId)?.Device.Name;

    private void OnSessionUnavailable(object? sender, EventArgs e)
    {
        if (sender is not SessionViewModel session) return;
        session.Unavailable -= OnSessionUnavailable;
        Sessions.Remove(session);
    }

    private void ClearDeviceState()
    {
        loadedDeviceId = null;
        HasMasterVolume = false;
        masterVolume = 0;
        OnPropertyChanged(nameof(MasterVolume));
        IsMasterMuted = false;
        MasterExternalChangeText = null;
        foreach (var session in Sessions) session.Unavailable -= OnSessionUnavailable;
        Sessions.Clear();
    }

    private void ApplyMasterVolume(AudioVolume value, bool force = false, bool isExternal = false)
    {
        if (force || !masterWriter.IsActive && (isExternal || !masterEchoGuard.IsStaleEcho(value.Level)))
        {
            masterVolume = Math.Round(value.Level * 100, 1);
            OnPropertyChanged(nameof(MasterVolume));
        }
        if (!isMasterMuteChanging) IsMasterMuted = value.IsMuted;
    }

    private async Task WriteMasterVolumeAsync((string DeviceId, float Level) request)
    {
        try
        {
            if (masterEditStarted is { } started) await started;
            var result = await controller.SetMasterVolumeAsync(request.DeviceId, request.Level);
            masterEchoGuard.Wrote(result.Level);
            if (!masterWriter.HasQueuedValue && loadedDeviceId == request.DeviceId) ApplyMasterVolume(result, force: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("调整系统主音量失败", exception);
            if (!masterWriter.HasQueuedValue && loadedDeviceId == request.DeviceId) await ReloadMasterVolumeAsync(request.DeviceId);
        }
    }

    private async Task ReloadMasterVolumeAsync(string deviceId)
    {
        try
        {
            var actual = await controller.GetMasterVolumeAsync(deviceId);
            if (loadedDeviceId == deviceId) ApplyMasterVolume(actual, force: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("回读系统主音量失败", exception);
        }
    }

    private async Task ToggleMasterMuteAsync()
    {
        if (loadedDeviceId is not { } deviceId) return;
        var target = !IsMasterMuted;
        isMasterMuteChanging = true;
        try
        {
            var result = await controller.SetMasterMuteAsync(deviceId, target);
            IsMasterMuted = result.IsMuted;
            Log.Info($"系统主音量：已{(result.IsMuted ? "静音" : "取消静音")}", "本软件");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error($"系统主音量{(target ? "静音" : "取消静音")}失败", exception);
            await ReloadMasterVolumeAsync(deviceId);
        }
        finally { isMasterMuteChanging = false; }
    }

    private void UpdateSyncState()
    {
        if (runtimeState is not { IsRunning: true })
        {
            SetSync(SyncState.Offline, "实时监听未运行");
            return;
        }
        var waiting = runtimeState.Devices.Count(device => device.Device.State.HasFlag(AudioDeviceState.Active) && !device.IsListening);
        if (waiting > 0) SetSync(SyncState.Degraded, $"实时同步中 · {waiting} 个设备监听恢复中");
        else SetSync(SyncState.Live, "实时同步中");
    }

    private void SetSync(SyncState state, string text)
    {
        SyncState = state;
        SyncStatusText = text;
    }

    private void SetDeviceStatus(string text, bool isWarning)
    {
        DeviceStatusText = text;
        IsDeviceWarning = isWarning;
    }

    private void SetReadIssues(IReadOnlyList<AudioReadIssue> issues)
    {
        if (issues.SequenceEqual(readIssues)) return;
        readIssues = issues;
        OnPropertyChanged(nameof(HasReadIssues));
        OnPropertyChanged(nameof(ReadIssueSummary));
        OnPropertyChanged(nameof(ReadIssueDetails));
    }

    /// <summary>停止读取通知；监听服务本身由容器在之后释放。</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Configuration is not null)
        {
            Configuration.Service.Changed -= OnConfigurationChanged;
            Configuration.Service.RestoreReadback -= OnRestoreReadback;
        }
        if (controller is AudioWriteGuard guard) guard.Changed -= UpdateControlAccess;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
