using System.Windows.Input;
using System.Windows.Media;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;

namespace AudioManager.Desktop.ViewModels;

/// <summary>单个应用音频会话。运行时按 <see cref="AudioSessionKey"/> 定位，不按 PID 合并；状态来自实时监听快照，写入经 IAudioController。</summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly IAudioController controller;
    private readonly OperationLog log;
    private readonly LatestValueWriter<float> volumeWriter;
    private string name = "";
    private string? processPath;
    private uint? processId;
    private bool isSystemSounds;
    private AudioSessionState state;
    private double volume;
    private bool isMuted;
    private bool isMuteChanging;
    private string? groupLabel;
    private ImageSource? icon;
    private string? externalChangeText;
    private readonly SelfEchoGuard echoGuard;
    private string? controlStatus;

    public SessionViewModel(AudioSession session, IAudioController controller, OperationLog log, TimeProvider? time = null)
    {
        Key = session.Key;
        this.controller = controller;
        this.log = log;
        echoGuard = new SelfEchoGuard(time);
        volumeWriter = new LatestValueWriter<float>(WriteVolumeAsync);
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync, () => CanControl && !isMuteChanging);
        Update(session);
    }

    /// <summary>会话已失效（如应用退出），应从列表中移除。</summary>
    public event EventHandler? Unavailable;

    public AudioSessionKey Key { get; }
    internal Task? VolumeEditStarted { get; set; }
    internal Task WaitForVolumeAsync() => volumeWriter.WhenIdleAsync();
    public ICommand ToggleMuteCommand { get; }
    public bool CanControl => controlStatus is null;
    public string? ControlStatus => controlStatus;
    internal void SetControlStatus(string? status)
    {
        if (SetProperty(ref controlStatus, status, nameof(ControlStatus)))
        {
            OnPropertyChanged(nameof(CanControl));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Name { get => name; private set => SetProperty(ref name, value); }
    public string? ProcessPath { get => processPath; private set => SetProperty(ref processPath, value); }
    public bool IsSystemSounds { get => isSystemSounds; private set => SetProperty(ref isSystemSounds, value); }
    public ImageSource? Icon { get => icon; set => SetProperty(ref icon, value); }

    public string DisplayName => IsSystemSounds ? "系统声音" : Name;

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (processId is { } pid && !IsSystemSounds) parts.Add($"PID {pid}");
            parts.Add(state switch
            {
                AudioSessionState.Active => "正在播放",
                AudioSessionState.Inactive => "空闲",
                _ => "已过期"
            });
            if (groupLabel is not null) parts.Add(groupLabel);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>界面音量 0～100，对应 API 标量 0～1。用户修改后写入系统并回读。</summary>
    public double Volume
    {
        get => volume;
        set
        {
            if (!CanControl) return;
            value = Math.Clamp(value, 0, 100);
            if (SetProperty(ref volume, value)) _ = volumeWriter.SubmitAsync((float)(value / 100));
        }
    }

    public bool IsMuted { get => isMuted; private set => SetProperty(ref isMuted, value); }

    /// <summary>同一程序的会话分组标识，用于识别多会话。</summary>
    internal string GroupKey => ProcessPath ?? (processId is { } pid ? $"pid:{pid}" : Key.InstanceId);

    internal void SetGroup(int index, int count)
    {
        groupLabel = count > 1 ? $"会话 {index}/{count}" : null;
        OnPropertyChanged(nameof(Detail));
    }

    /// <summary>最近一次外部修改（Windows 混合器或其他程序）的提示；本软件自身写入不显示。</summary>
    public string? ExternalChangeText { get => externalChangeText; private set => SetProperty(ref externalChangeText, value); }

    internal void MarkExternalChange(DateTime time) => ExternalChangeText = $"外部修改 {time:HH:mm:ss}";

    /// <summary>
    /// 应用实时快照；正在写入的值不被旧快照覆盖。<paramref name="isExternal"/> 为 true 表示快照来自外部修改，
    /// 此时不按本软件写入回传过滤。
    /// </summary>
    internal void Update(AudioSession session, bool isExternal = false)
    {
        Name = session.Name;
        ProcessPath = session.ProcessPath;
        processId = session.ProcessId;
        IsSystemSounds = session.IsSystemSounds;
        state = session.State;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Detail));
        ApplyVolume(session.Volume, isExternal);
    }

    private void ApplyVolume(AudioVolume value, bool isExternal)
    {
        if (!volumeWriter.IsActive && (isExternal || !echoGuard.IsStaleEcho(value.Level)))
        {
            volume = Math.Round(value.Level * 100, 1);
            OnPropertyChanged(nameof(Volume));
        }
        if (!isMuteChanging) IsMuted = value.IsMuted;
    }

    internal void AcceptRestoreReadback(AudioVolume value)
    {
        echoGuard.Wrote(value.Level);
        ApplyVolume(value, isExternal: true);
    }

    private async Task WriteVolumeAsync(float level)
    {
        try
        {
            if (VolumeEditStarted is { } started) await started;
            var result = await controller.SetSessionVolumeAsync(Key, level);
            echoGuard.Wrote(result.Level);
            if (!volumeWriter.HasQueuedValue)
            {
                volume = Math.Round(result.Level * 100, 1);
                OnPropertyChanged(nameof(Volume));
                if (!isMuteChanging) IsMuted = result.IsMuted;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (HandleFailure("调整音量", exception) && !volumeWriter.HasQueuedValue) await ReloadVolumeAsync();
        }
    }

    /// <summary>写入失败后回读实际值，使滑块不停留在未生效的位置。</summary>
    private async Task ReloadVolumeAsync()
    {
        try
        {
            var actual = await controller.GetSessionVolumeAsync(Key);
            volume = Math.Round(actual.Level * 100, 1);
            OnPropertyChanged(nameof(Volume));
            IsMuted = actual.IsMuted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HandleFailure("回读音量", exception);
        }
    }

    private async Task ToggleMuteAsync()
    {
        var target = !IsMuted;
        isMuteChanging = true;
        try
        {
            var result = await controller.SetSessionMuteAsync(Key, target);
            IsMuted = result.IsMuted;
            log.Info($"{DisplayName}：已{(result.IsMuted ? "静音" : "取消静音")}", "本软件");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HandleFailure(target ? "静音" : "取消静音", exception);
            await ReloadVolumeAsync();
        }
        finally { isMuteChanging = false; }
    }

    /// <summary>记录失败；会话已失效时通知移除并返回 false。</summary>
    private bool HandleFailure(string action, Exception exception)
    {
        if (exception is AudioControlException { Code: AudioErrorCode.SessionUnavailable })
        {
            if (Unavailable is not null) log.Info($"{DisplayName}：会话已失效（应用可能已退出），已从列表移除");
            Unavailable?.Invoke(this, EventArgs.Empty);
            return false;
        }
        log.Error($"{DisplayName}：{action}失败", exception);
        return true;
    }
}
