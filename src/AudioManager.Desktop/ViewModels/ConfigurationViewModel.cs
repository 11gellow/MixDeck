using System.Collections.ObjectModel;
using System.Windows.Input;
using AudioManager.Core.Configuration;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Services;
using AudioManager.Desktop.Services;

namespace AudioManager.Desktop.ViewModels;

public sealed class ConfigurationViewModel : ObservableObject, IDisposable
{
    private readonly AudioConfigurationService service;
    private readonly IAudioStateMonitor monitor;
    private readonly ILoginStartup startup;
    private readonly IConfigurationDialogs dialogs;
    private readonly OperationLog log;
    private bool initialized;
    private bool busy;
    private AudioProfile? selectedProfile;
    private string status = "尚未加载配置";
    private bool startAtLogin;
    private bool showUnavailable;
    private string? deviceId;
    private string deviceName = "当前默认输出设备";
    private bool reloading;
    private Task pending = Task.CompletedTask;
    private int queued;
    public Func<Task>? BeforeApply { get; set; }
    public Task WhenIdleAsync() => pending;

    public ConfigurationViewModel(AudioConfigurationService service, IAudioStateMonitor monitor, ILoginStartup startup, IConfigurationDialogs dialogs, OperationLog log)
    {
        this.service = service;
        this.monitor = monitor;
        this.startup = startup;
        this.dialogs = dialogs;
        this.log = log;
        RestoreCommand = Command(async () =>
        {
            await service.RestoreSnapshotAsync(await monitor.GetStateAsync(), SelectedProfile!.Id, deviceId);
        }, () => SelectedProfile is not null);
        DeleteCommand = Command(async () =>
        {
            if (dialogs.Confirm($"删除方案“{SelectedProfile!.Name}”？当前系统音量不会被重置。")) await service.DeleteProfileAsync(SelectedProfile.Id);
        }, () => SelectedProfile is not null);
        ImportCommand = Command(async () =>
        {
            var path = dialogs.ChooseImport();
            if (path is not null && dialogs.Confirm("导入将替换全部方案和偏好，并应用各设备对应方案；原配置保留副本，登录启动沿用本机设置。继续？"))
            {
                await service.ImportAsync(path);
                Status = "已导入并应用各设备对应方案";
            }
        });
        ExportCommand = Command(async () => { if (dialogs.ChooseExport() is { } path) { await service.ExportAsync(path); Status = "配置已导出"; } });
        ResetCommand = Command(async () =>
        {
            if (!dialogs.Confirm("恢复默认将清空方案、关闭登录启动，并保留原配置副本。之后会重新记住各设备的用户调节。继续？")) return;
            var previous = startup.IsEnabled;
            startup.SetEnabled(false);
            try { await service.ResetAsync(); }
            catch { startup.SetEnabled(previous); throw; }
            Status = "已恢复默认；系统当前音量保持不变";
        });
        service.Changed += Reload;
        service.Reported += Report;
    }

    public AudioConfigurationService Service => service;
    public ObservableCollection<AudioProfile> Profiles { get; } = [];
    public ObservableCollection<RestoreRuleViewModel> Rules { get; } = [];
    public AudioProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (reloading || value is null || value.Id == selectedProfile?.Id) return;
            var id = value.Id;
            var device = deviceId;
            selectedProfile = value;
            _ = QueueAsync(async () =>
            {
                if (BeforeApply is not null) await BeforeApply();
                await service.SelectProfileAsync(id, device);
                Status = "已自动保存并应用所选方案";
            });
        }
    }
    public string SelectedProfileDetails => SelectedProfile is { } profile ? $"{profile.Snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 原始快照 {profile.Snapshot.Entries.Count} 个目标 · 当前设备 {Rules.Count} 条规则" : "尚未选择方案";
    public string CurrentProfileName => (deviceId is null ? service.Current.Profiles.FirstOrDefault(profile => profile.Id == service.Current.ActiveProfileId)
        : service.ProfileForDevice(deviceId))?.Name ?? "无（首次使用会自动建立）";
    public string CurrentDeviceName => deviceName;
    public void SetDevice(string? id, string? name)
    {
        if (deviceId == id && deviceName == (name ?? "当前默认输出设备")) return;
        _ = CommitInputsAsync();
        deviceId = id;
        deviceName = name ?? "当前默认输出设备";
        selectedProfile = null;
        Reload();
        OnPropertyChanged(nameof(CurrentDeviceName));
    }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool StartAtLogin { get => startAtLogin; set
    {
        if (reloading || !SetProperty(ref startAtLogin, value)) return;
        _ = QueueAsync(async () =>
        {
            var previous = startup.IsEnabled;
            startup.SetEnabled(value);
            try { await service.UpdateSettingAsync(startAtLogin: value); }
            catch { startup.SetEnabled(previous); throw; }
            Status = "登录启动设置已自动保存";
        });
    } }
    public bool ShowUnavailableDevices { get => showUnavailable; set
    {
        if (reloading || !SetProperty(ref showUnavailable, value)) return;
        _ = QueueAsync(async () => { await service.UpdateSettingAsync(showUnavailableDevices: value); Status = "显示设置已自动保存"; });
    } }
    public string ConfigurationPath => service.FilePath;
    public bool IsBusy { get => busy; private set { SetProperty(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }

    public async Task InitializeAsync()
    {
        if (initialized) return;
        await QueueAsync(async () =>
        {
            await service.InitializeAsync();
            initialized = true;
            var actualStartup = startup.IsEnabled;
            startAtLogin = actualStartup;
            OnPropertyChanged(nameof(StartAtLogin));
            Status = service.LoadResult?.Warning ?? "配置已加载";
            if (service.Current.Settings.StartAtLogin != actualStartup)
            {
                Status += "；登录启动状态与配置不同，已显示注册表实际值，修改开关时自动同步";
            }
            if (service.LoadResult?.Warning is { } warning) log.Warning(warning, "配置");
        });
    }

    private AsyncRelayCommand Command(Func<Task> action, Func<bool>? canExecute = null)
        => new(() => QueueAsync(async () => { if (BeforeApply is not null) await BeforeApply(); await action(); }),
            () => !IsBusy && (canExecute?.Invoke() ?? true));

    private Task QueueAsync(Func<Task> action)
    {
        var previous = pending;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion.Task;
        queued++;
        IsBusy = true;
        _ = RunQueuedAsync();
        return completion.Task;
        async Task RunQueuedAsync()
        {
            try { await previous; await action(); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Status = exception.Message;
                log.Error("配置自动保存或操作失败", exception);
                Reload();
            }
            finally { if (--queued == 0) IsBusy = false; completion.TrySetResult(); }
        }
    }

    public async Task CommitInputsAsync()
    {
        foreach (var row in Rules.ToArray()) await row.CommitVolumeAsync();
        await WhenIdleAsync();
    }

    private Task EditRuleAsync(Guid profile, string device, Guid rule, float? level, bool? muted, bool? enabled)
        => QueueAsync(async () =>
        {
            await service.UpdateRuleAsync(profile, device, rule, level, muted, enabled);
            Status = "规则已自动保存；原始快照保持不变";
        });

    private void Reload()
    {
        reloading = true;
        try
        {
            var currentId = deviceId is null ? service.Current.ActiveProfileId : service.ProfileForDevice(deviceId)?.Id;
            var visible = service.Current.Profiles.Where(profile => deviceId is null || profile.Rules.Any(rule =>
                string.Equals(rule.Target.Target.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) || profile.Snapshot.Entries.Any(entry =>
                string.Equals(entry.Target.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (!Profiles.SequenceEqual(visible))
            {
                Profiles.Clear();
                foreach (var profile in visible) Profiles.Add(profile);
            }
            selectedProfile = Profiles.FirstOrDefault(profile => profile.Id == currentId);
            var entries = (selectedProfile?.Rules ?? []).Where(rule => deviceId is null || string.Equals(rule.Target.Target.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var existing = Rules.ToDictionary(row => row.Id);
            var rows = new List<RestoreRuleViewModel>();
            foreach (var rule in entries)
            {
                if (existing.TryGetValue(rule.Id, out var row)) row.Update(rule);
                else
                {
                    var profileId = selectedProfile!.Id;
                    row = new(rule, service.Current.KnownDevices.FirstOrDefault(device => device.Id == rule.Target.Target.DeviceId)?.Name,
                        (level, muted, enabled) => EditRuleAsync(profileId, rule.Target.Target.DeviceId, rule.Id, level, muted, enabled),
                        message => Status = message);
                }
                rows.Add(row);
            }
            foreach (var removed in Rules.Except(rows).ToArray()) { removed.CancelVolume(); Rules.Remove(removed); }
            for (var i = 0; i < rows.Count; i++)
            {
                var index = Rules.IndexOf(rows[i]);
                if (index < 0) Rules.Insert(i, rows[i]);
                else if (index != i) Rules.Move(index, i);
            }
            startAtLogin = startup.IsEnabled;
            showUnavailable = service.Current.Settings.ShowUnavailableDevices;
            OnPropertyChanged(nameof(StartAtLogin));
            OnPropertyChanged(nameof(ShowUnavailableDevices));
            OnPropertyChanged(nameof(SelectedProfile));
            OnPropertyChanged(nameof(SelectedProfileDetails));
            OnPropertyChanged(nameof(CurrentProfileName));
            CommandManager.InvalidateRequerySuggested();
        }
        finally { reloading = false; }
    }

    private void Report(RestoreResult result)
    {
        Status = result.Target + "：" + result.Message;
        var source = result.Message.Contains("自动保存", StringComparison.Ordinal) ? "自动保存" : "配置恢复";
        if (result.Succeeded) log.Info(Status, source);
        else log.Warning(Status, source);
    }

    public void Dispose() { service.Changed -= Reload; service.Reported -= Report; }
}

public sealed class RestoreRuleViewModel : ObservableObject
{
    private RestoreRule original;
    private readonly Func<float?, bool?, bool?, Task>? edit;
    private readonly Action<string>? report;
    private bool editingVolume;
    private bool enabled;
    private bool muted;
    private string volumeText = "";
    public RestoreRuleViewModel(RestoreRule rule, string? deviceName = null,
        Func<float?, bool?, bool?, Task>? edit = null, Action<string>? report = null)
    {
        original = rule;
        this.edit = edit;
        this.report = report;
        DeviceName = deviceName ?? rule.Target.Target.DeviceId;
        Update(rule);
    }
    public Guid Id => original.Id;
    public string Name => original.Target.DisplayName;
    public string Match => original.Target.Target.Application?.Value ?? "系统主音量";
    public string Device => original.Target.Target.DeviceId;
    public string DeviceName { get; }
    public bool Enabled { get => enabled; set { if (SetProperty(ref enabled, value) && edit is not null) _ = edit(null, null, value); } }
    public bool IsMuted { get => muted; set { if (SetProperty(ref muted, value) && edit is not null) _ = edit(null, value, null); } }
    public string VolumeText { get => volumeText; set { editingVolume = true; SetProperty(ref volumeText, value); } }
    public void Update(RestoreRule rule)
    {
        original = rule;
        enabled = rule.Enabled;
        muted = rule.Target.Volume.IsMuted;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(IsMuted));
        if (!editingVolume) CancelVolume();
    }
    public void CancelVolume()
    {
        editingVolume = false;
        volumeText = Math.Round(original.Target.Volume.Level * 100, 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(VolumeText));
    }
    public async Task CommitVolumeAsync()
    {
        if (!editingVolume) return;
        var text = volumeText;
        editingVolume = false;
        if (!double.TryParse(text, out var parsed) || !double.IsFinite(parsed) || parsed < 0 || parsed > 100)
        {
            CancelVolume();
            report?.Invoke($"{Name} 的目标音量必须在 0～100 之间，已恢复已保存值。");
            return;
        }
        if (edit is not null) await edit((float)(parsed / 100), null, null);
        CancelVolume();
    }
}
