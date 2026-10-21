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
    private string profileName = "新方案";
    private string status = "尚未加载配置";
    private bool automaticRestore;
    private bool startAtLogin;
    private bool showUnavailable;

    public ConfigurationViewModel(AudioConfigurationService service, IAudioStateMonitor monitor, ILoginStartup startup, IConfigurationDialogs dialogs, OperationLog log)
    {
        this.service = service;
        this.monitor = monitor;
        this.startup = startup;
        this.dialogs = dialogs;
        this.log = log;
        SaveSnapshotCommand = Command(SaveSnapshotAsync);
        ActivateCommand = Command(async () => { await service.SelectProfileAsync(SelectedProfile!.Id); Status = "已选择当前方案"; }, () => SelectedProfile is not null);
        RestoreCommand = Command(async () =>
        {
            if (service.Current.Settings.AutomaticRestoreEnabled)
                throw new InvalidOperationException("请先停用自动恢复并保存设置，再手动恢复原始快照。");
            await service.RestoreSnapshotAsync(await monitor.GetStateAsync(), SelectedProfile!.Id);
        }, () => SelectedProfile is not null);
        DeleteCommand = Command(async () =>
        {
            if (dialogs.Confirm($"删除方案“{SelectedProfile!.Name}”？当前系统音量不会被重置。")) await service.DeleteProfileAsync(SelectedProfile.Id);
        }, () => SelectedProfile is not null);
        SaveRulesCommand = Command(async () => { await service.UpdateRulesAsync(SelectedProfile!.Id, Rules.Select(row => row.ToRule()).ToArray()); Status = "规则目标已保存；原始快照保持不变"; }, () => SelectedProfile is not null);
        SaveSettingsCommand = Command(SaveSettingsAsync);
        ImportCommand = Command(async () =>
        {
            var path = dialogs.ChooseImport();
            if (path is not null && dialogs.Confirm("导入将替换全部方案和偏好，并保留原配置副本；自动恢复将关闭。继续？"))
            {
                await service.ImportAsync(path);
                Status = "已导入；自动恢复已关闭，请检查方案后启用";
            }
        });
        ExportCommand = Command(async () => { if (dialogs.ChooseExport() is { } path) { await service.ExportAsync(path); Status = "配置已导出"; } });
        ResetCommand = Command(async () =>
        {
            if (!dialogs.Confirm("恢复默认将清空方案、关闭自动恢复和登录启动，并保留原配置副本。继续？")) return;
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
            if (!SetProperty(ref selectedProfile, value)) return;
            Rules.Clear();
            foreach (var rule in value?.Rules ?? []) Rules.Add(new(rule,
                service.Current.KnownDevices.FirstOrDefault(device => device.Id == rule.Target.Target.DeviceId)?.Name));
            OnPropertyChanged(nameof(SelectedProfileDetails));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SelectedProfileDetails => SelectedProfile is { } profile ? $"{profile.Snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {profile.Snapshot.Entries.Count} 个快照目标 · {profile.Rules.Count} 条规则" : "尚未选择方案";
    public string CurrentProfileName => service.Current.Profiles.FirstOrDefault(profile => profile.Id == service.Current.ActiveProfileId)?.Name ?? "无";
    public string ProfileName { get => profileName; set => SetProperty(ref profileName, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool AutomaticRestore { get => automaticRestore; set => SetProperty(ref automaticRestore, value); }
    public bool StartAtLogin { get => startAtLogin; set => SetProperty(ref startAtLogin, value); }
    public bool ShowUnavailableDevices { get => showUnavailable; set => SetProperty(ref showUnavailable, value); }
    public string ConfigurationPath => service.FilePath;
    public bool IsBusy { get => busy; private set { SetProperty(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public AsyncRelayCommand SaveSnapshotCommand { get; }
    public AsyncRelayCommand ActivateCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand SaveRulesCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }

    public async Task InitializeAsync()
    {
        if (initialized) return;
        await RunAsync(async () =>
        {
            await service.InitializeAsync();
            initialized = true;
            var actualStartup = startup.IsEnabled;
            StartAtLogin = actualStartup;
            Status = service.LoadResult?.Warning ?? "配置已加载";
            if (service.Current.Settings.StartAtLogin != actualStartup)
            {
                Status += "；登录启动状态与配置不同，已显示注册表实际值，请保存设置以同步";
            }
            if (service.LoadResult?.Warning is { } warning) log.Warning(warning, "配置");
        });
    }

    private async Task SaveSnapshotAsync()
    {
        await service.SaveSnapshotAsync(await monitor.GetStateAsync(), ProfileName);
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == service.Current.ActiveProfileId);
        Status = "已保存新方案并设为当前方案";
    }

    private async Task SaveSettingsAsync()
    {
        if (AutomaticRestore && service.Current.ActiveProfileId is null) throw new InvalidOperationException("请先保存或选择当前方案，再启用自动恢复。");
        var settings = new SoftwareSettings { AutomaticRestoreEnabled = AutomaticRestore, StartAtLogin = StartAtLogin, ShowUnavailableDevices = ShowUnavailableDevices };
        var previous = startup.IsEnabled;
        startup.SetEnabled(settings.StartAtLogin);
        try { await service.UpdateSettingsAsync(settings); }
        catch { startup.SetEnabled(previous); throw; }
        Status = "设置已保存";
    }

    private AsyncRelayCommand Command(Func<Task> action, Func<bool>? canExecute = null)
        => new(() => RunAsync(action), () => !IsBusy && (canExecute?.Invoke() ?? true));
    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try { await action(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            log.Error("配置操作失败", exception);
            Reload();
        }
        finally { IsBusy = false; }
    }

    private void Reload()
    {
        var selectedId = SelectedProfile?.Id;
        // 恢复报告也触发服务 Changed；不要覆盖用户尚未提交的规则编辑。
        if (Profiles.Count != service.Current.Profiles.Count || !Profiles.SequenceEqual(service.Current.Profiles))
        {
            Profiles.Clear();
            foreach (var profile in service.Current.Profiles) Profiles.Add(profile);
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedId)
                ?? Profiles.FirstOrDefault(profile => profile.Id == service.Current.ActiveProfileId) ?? Profiles.FirstOrDefault();
        }
        AutomaticRestore = service.Current.Settings.AutomaticRestoreEnabled;
        StartAtLogin = service.Current.Settings.StartAtLogin;
        ShowUnavailableDevices = service.Current.Settings.ShowUnavailableDevices;
        OnPropertyChanged(nameof(CurrentProfileName));
    }

    private void Report(RestoreResult result)
    {
        Status = result.Target + "：" + result.Message;
        if (result.Succeeded) log.Info(Status, "配置恢复");
        else log.Warning(Status, "配置恢复");
    }

    public void Dispose() { service.Changed -= Reload; service.Reported -= Report; }
}

public sealed class RestoreRuleViewModel : ObservableObject
{
    private readonly RestoreRule original;
    private bool enabled;
    private string volumeText;
    private bool muted;
    public RestoreRuleViewModel(RestoreRule rule, string? deviceName = null)
    {
        original = rule;
        enabled = rule.Enabled;
        volumeText = Math.Round(rule.Target.Volume.Level * 100, 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        muted = rule.Target.Volume.IsMuted;
        DeviceName = deviceName ?? rule.Target.Target.DeviceId;
    }
    public string Name => original.Target.DisplayName;
    public string Match => original.Target.Target.Application?.Value ?? "系统主音量";
    public string Device => original.Target.Target.DeviceId;
    public string DeviceName { get; }
    public bool Enabled { get => enabled; set => SetProperty(ref enabled, value); }
    public string VolumeText { get => volumeText; set => SetProperty(ref volumeText, value); }
    public bool IsMuted { get => muted; set => SetProperty(ref muted, value); }
    public RestoreRule ToRule()
    {
        if (!double.TryParse(VolumeText, out var parsed) || !double.IsFinite(parsed) || parsed < 0 || parsed > 100)
            throw new InvalidOperationException($"{Name} 的目标音量必须在 0～100 之间。");
        return original with { Enabled = Enabled, Target = original.Target with { Volume = new((float)(parsed / 100), IsMuted) } };
    }
}
