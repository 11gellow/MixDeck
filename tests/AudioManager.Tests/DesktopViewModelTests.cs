using System.Runtime.CompilerServices;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Desktop.Services;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Tests;

public sealed class DesktopViewModelTests
{
    private const string DeviceA = "device-a";
    private const string DeviceB = "device-b";

    [Fact]
    public async Task SelectingOutputChangesWindowsDefaultInsteadOfOnlyDisplayingItsSessions()
    {
        var (monitor, controller, viewModel) = Create();
        monitor.AddDevice(DeviceB, "Headphones");
        await viewModel.InitializeAsync();
        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == DeviceB);
        await viewModel.OutputSwitchTask;
        Assert.Equal(new[] { DeviceB }, controller.DefaultWrites);
        Assert.Equal(DeviceB, (await monitor.GetStateAsync()).DefaultOutputDevices[AudioDeviceRole.Multimedia]);
        Assert.True(viewModel.SelectedDevice?.IsDefault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrUnconfirmedOutputSelectionKeepsActualDefaultAndReportsFailure(bool ineffective)
    {
        var (monitor, controller, viewModel) = Create();
        monitor.AddDevice(DeviceB, "Headphones");
        await viewModel.InitializeAsync();
        controller.IgnoreDefaultWrites = ineffective;
        controller.DefaultFailure = ineffective ? null : new UnauthorizedAccessException("系统拒绝切换");
        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == DeviceB);
        await viewModel.OutputSwitchTask;
        Assert.Equal(DeviceA, viewModel.SelectedDevice?.Id);
        Assert.True(viewModel.SelectedDevice?.IsDefault);
        Assert.Contains("切换失败", viewModel.DeviceStatusText);
        Assert.False(viewModel.IsSwitchingOutput);
        Assert.True(viewModel.CanSelectOutput);
        Assert.True(viewModel.CanControlMaster);
        Assert.Contains(viewModel.Log.Entries, entry => entry.IsError);
    }

    [Fact]
    public async Task PendingOutputSelectionDisablesControlsAndRefreshEventsDoNotWriteBack()
    {
        var (monitor, controller, viewModel) = Create(Session(DeviceA, "a", "Player", .5f));
        monitor.AddDevice(DeviceB, "Headphones");
        await viewModel.InitializeAsync();
        controller.DefaultGate = new();
        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == DeviceB);
        Assert.True(viewModel.IsSwitchingOutput);
        Assert.False(viewModel.CanSelectOutput);
        Assert.False(viewModel.CanControlMaster);
        Assert.False(viewModel.Sessions[0].CanControl);
        Assert.Equal(DeviceA, viewModel.SelectedDevice?.Id);
        monitor.Push(AudioStateChangeKind.DevicesChanged, AudioChangeOrigin.System);
        Assert.Single(controller.DefaultWrites);
        controller.DefaultGate.SetResult();
        await viewModel.OutputSwitchTask;
        Assert.Equal(DeviceB, viewModel.SelectedDevice?.Id);
        Assert.True(viewModel.SelectedDevice?.IsDefault);
        Assert.True(viewModel.CanControlMaster);
        monitor.SetDefault(DeviceA);
        monitor.Push(AudioStateChangeKind.DefaultDeviceChanged, AudioChangeOrigin.System, DeviceA);
        Assert.Equal(DeviceA, viewModel.SelectedDevice?.Id);
        Assert.Single(controller.DefaultWrites);
    }

    [Fact]
    public async Task FailedMasterWriteReportsErrorAndReloadsActualVolume()
    {
        var (_, controller, viewModel) = Create();
        await viewModel.InitializeAsync();
        controller.FailMasterWrites = new UnauthorizedAccessException("测试主音量写入失败");
        viewModel.MasterVolume = 30;
        Assert.Equal(50, viewModel.MasterVolume);
        Assert.True(viewModel.Log.Latest?.IsError);
        Assert.Contains("调整系统主音量失败", viewModel.Log.Latest?.Text);
    }

    [Fact]
    public async Task FailedMasterMuteReportsErrorWithoutInventingMutedState()
    {
        var (_, controller, viewModel) = Create();
        await viewModel.InitializeAsync();
        controller.FailMasterWrites = new UnauthorizedAccessException("测试主静音写入失败");
        await ((AsyncRelayCommand)viewModel.ToggleMasterMuteCommand).ExecuteAsync();
        Assert.False(viewModel.IsMasterMuted);
        Assert.True(viewModel.Log.Latest?.IsError);
        Assert.Contains("静音失败", viewModel.Log.Latest?.Text);
    }

    [Fact]
    public async Task StartShowsDefaultDeviceMasterVolumeAndLiveSessionsWithoutWriting()
    {
        var (monitor, controller, viewModel) = Create(
            Session(DeviceA, "b", "Zeta", 0.4f),
            Session(DeviceA, "a", "Alpha", 0.25f, isMuted: true),
            Session(DeviceA, "x", "Gone", 1f, AudioSessionState.Expired));

        await viewModel.InitializeAsync();

        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(DeviceA, viewModel.SelectedDevice?.Id);
        Assert.True(viewModel.SelectedDevice?.IsDefault);
        Assert.True(viewModel.HasMasterVolume);
        Assert.Equal(50, viewModel.MasterVolume);
        Assert.Equal(new[] { "Alpha", "Zeta" }, viewModel.Sessions.Select(session => session.DisplayName));
        Assert.Equal(25, viewModel.Sessions[0].Volume);
        Assert.True(viewModel.Sessions[0].IsMuted);
        Assert.Equal(SyncState.Live, viewModel.SyncState);
        Assert.Empty(controller.Writes);
    }

    [Fact]
    public async Task SessionsOfTheSameProgramAreLabelledSeparately()
    {
        var (_, _, viewModel) = Create(
            Session(DeviceA, "1", "Player", 0.5f, path: @"C:\apps\player.exe", pid: 10),
            Session(DeviceA, "2", "Player", 0.6f, path: @"C:\apps\player.exe", pid: 10),
            Session(DeviceA, "3", "Other", 0.7f, path: @"C:\apps\other.exe", pid: 11));

        await viewModel.InitializeAsync();

        Assert.DoesNotContain("会话", viewModel.Sessions.Single(session => session.DisplayName == "Other").Detail);
        var players = viewModel.Sessions.Where(session => session.DisplayName == "Player").ToList();
        Assert.Contains("会话 1/2", players[0].Detail);
        Assert.Contains("会话 2/2", players[1].Detail);
    }

    [Fact]
    public async Task DraggingCoalescesWritesToLatestValueAndTargetsOnlyThatSession()
    {
        var (_, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f), Session(DeviceA, "b", "Beta", 0.5f));
        await viewModel.InitializeAsync();
        var alpha = viewModel.Sessions.Single(session => session.DisplayName == "Alpha");
        var beta = viewModel.Sessions.Single(session => session.DisplayName == "Beta");
        controller.HoldWrites = true;

        alpha.Volume = 10;
        alpha.Volume = 20;
        alpha.Volume = 30;
        Assert.Single(controller.Writes);

        controller.ReleaseNextWrite();
        controller.ReleaseNextWrite();

        Assert.Equal(new[] { 0.1f, 0.3f }, controller.Writes.Select(write => write.Level));
        Assert.All(controller.Writes, write => Assert.Equal(alpha.Key, write.Key));
        Assert.Equal(30, alpha.Volume);
        Assert.Equal(50, beta.Volume);
    }

    [Fact]
    public async Task ExternalSessionChangeIsShownLiveWithoutWritingBack()
    {
        var (monitor, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();
        var alpha = viewModel.Sessions[0];

        monitor.SetSession(Session(DeviceA, "a", "Alpha", 0.2f, isMuted: true));
        monitor.Push(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, DeviceA, alpha.Key);

        Assert.Same(alpha, viewModel.Sessions[0]);
        Assert.Equal(20, alpha.Volume);
        Assert.True(alpha.IsMuted);
        Assert.NotNull(alpha.ExternalChangeText);
        Assert.Equal("外部", viewModel.Log.Latest?.Source);
        Assert.Contains("Alpha", viewModel.Log.Latest?.Text);
        Assert.Empty(controller.Writes);
        Assert.Equal(0, monitor.RefreshCount);
    }

    [Fact]
    public async Task RepeatedExternalChangesAreCoalescedInTheLog()
    {
        var (monitor, _, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();
        var before = viewModel.Log.Entries.Count;

        foreach (var level in new[] { 0.4f, 0.3f, 0.2f })
        {
            monitor.SetSession(Session(DeviceA, "a", "Alpha", level));
            monitor.Push(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, DeviceA, viewModel.Sessions[0].Key);
        }

        Assert.Equal(before + 1, viewModel.Log.Entries.Count);
        Assert.Contains("20%", viewModel.Log.Latest?.Text);
    }

    [Fact]
    public async Task SelfEventsDoNotMarkExternalOrMoveSliderBackToStaleValue()
    {
        var (monitor, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();
        var alpha = viewModel.Sessions[0];
        controller.HoldWrites = true;
        alpha.Volume = 10;
        alpha.Volume = 30;
        controller.ReleaseNextWrite();
        controller.ReleaseNextWrite();

        // 系统按顺序回传两次写入；第一条回传到达时快照仍是较早的 10%。
        monitor.SetSession(Session(DeviceA, "a", "Alpha", 0.1f));
        monitor.Push(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, DeviceA, alpha.Key);
        Assert.Equal(30, alpha.Volume);
        Assert.Null(alpha.ExternalChangeText);

        monitor.SetSession(Session(DeviceA, "a", "Alpha", 0.3f));
        monitor.Push(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, DeviceA, alpha.Key);
        Assert.Equal(30, alpha.Volume);

        // 外部修改不受本软件回传过滤影响。
        monitor.SetSession(Session(DeviceA, "a", "Alpha", 0.6f));
        monitor.Push(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, DeviceA, alpha.Key);
        Assert.Equal(60, alpha.Volume);
        Assert.Equal(2, controller.Writes.Count);
    }

    [Fact]
    public async Task SnapshotDuringDragDoesNotOverwriteSliderOrTriggerWrite()
    {
        var (monitor, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();
        var alpha = viewModel.Sessions[0];
        controller.HoldWrites = true;

        alpha.Volume = 80;
        monitor.Push(AudioStateChangeKind.SessionStateChanged, AudioChangeOrigin.System, DeviceA, alpha.Key);

        Assert.Equal(80, alpha.Volume);
        Assert.Single(controller.Writes);
        controller.ReleaseNextWrite();
        Assert.Equal(80, alpha.Volume);
        Assert.Single(controller.Writes);
    }

    [Fact]
    public async Task CreatedAndRemovedSessionsUpdateTheListAutomatically()
    {
        var (monitor, _, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();

        var created = Session(DeviceA, "n", "Newcomer", 0.7f);
        monitor.SetSession(created);
        monitor.Push(AudioStateChangeKind.SessionCreated, AudioChangeOrigin.System, DeviceA, created.Key);
        Assert.Contains(viewModel.Sessions, session => session.DisplayName == "Newcomer");
        Assert.Contains("Newcomer", viewModel.Log.Latest?.Text);

        monitor.RemoveSession(created.Key);
        monitor.Push(AudioStateChangeKind.SessionRemoved, AudioChangeOrigin.System, DeviceA, created.Key);
        Assert.DoesNotContain(viewModel.Sessions, session => session.DisplayName == "Newcomer");
        Assert.Contains("已结束：Newcomer", viewModel.Log.Latest?.Text);
    }

    [Fact]
    public async Task ExternalMasterChangeIsShownAndMarked()
    {
        var (monitor, controller, viewModel) = Create();
        await viewModel.InitializeAsync();

        monitor.SetMaster(DeviceA, new AudioVolume(0.8f, true));
        monitor.Push(AudioStateChangeKind.MasterVolumeChanged, AudioChangeOrigin.External, DeviceA);

        Assert.Equal(80, viewModel.MasterVolume);
        Assert.True(viewModel.IsMasterMuted);
        Assert.NotNull(viewModel.MasterExternalChangeText);
        Assert.Empty(controller.MasterWrites);
    }

    [Fact]
    public async Task DefaultDeviceChangeAlwaysFollowsWindowsAndUserSelectionWritesSystem()
    {
        var (monitor, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        monitor.AddDevice(DeviceB, "Headphones", sessions: [Session(DeviceB, "h", "Music", 0.3f)]);
        await viewModel.InitializeAsync();

        monitor.SetDefault(DeviceB);
        monitor.Push(AudioStateChangeKind.DefaultDeviceChanged, AudioChangeOrigin.External, DeviceB, message: nameof(AudioDeviceRole.Multimedia));
        Assert.Equal(DeviceB, viewModel.SelectedDevice?.Id);
        Assert.Equal(new[] { "Music" }, viewModel.Sessions.Select(session => session.DisplayName));
        Assert.Contains("Headphones", viewModel.Log.Latest?.Text);

        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == DeviceA);
        await viewModel.OutputSwitchTask;
        Assert.Equal(new[] { DeviceA }, controller.DefaultWrites);
        monitor.SetDefault(DeviceA);
        monitor.Push(AudioStateChangeKind.DefaultDeviceChanged, AudioChangeOrigin.External, DeviceA, message: nameof(AudioDeviceRole.Multimedia));
        monitor.SetDefault(DeviceB);
        monitor.Push(AudioStateChangeKind.DefaultDeviceChanged, AudioChangeOrigin.External, DeviceB, message: nameof(AudioDeviceRole.Multimedia));
        Assert.Equal(DeviceB, viewModel.SelectedDevice?.Id);
    }

    [Fact]
    public async Task VolumeEventsDoNotRebuildTheDeviceList()
    {
        var (monitor, _, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();
        var item = viewModel.Devices[0];

        monitor.SetMaster(DeviceA, new AudioVolume(0.3f, false));
        monitor.Push(AudioStateChangeKind.MasterVolumeChanged, AudioChangeOrigin.External, DeviceA);

        Assert.Same(item, viewModel.Devices[0]);
        Assert.Same(item, viewModel.SelectedDevice);
    }

    [Fact]
    public async Task LostListeningDisablesControlsUntilRecovery()
    {
        var (monitor, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();

        monitor.SetListening(DeviceA, false);
        monitor.Push(AudioStateChangeKind.DeviceListeningChanged, AudioChangeOrigin.System, DeviceA);
        Assert.False(viewModel.HasMasterVolume);
        Assert.Empty(viewModel.Sessions);
        Assert.Equal(SyncState.Degraded, viewModel.SyncState);
        Assert.True(viewModel.IsDeviceWarning);
        Assert.True(viewModel.Log.Latest?.IsError);
        viewModel.MasterVolume = 90;
        Assert.Empty(controller.MasterWrites);

        monitor.SetListening(DeviceA, true);
        monitor.Push(AudioStateChangeKind.DeviceListeningChanged, AudioChangeOrigin.Recovery, DeviceA);
        Assert.True(viewModel.HasMasterVolume);
        Assert.Single(viewModel.Sessions);
        Assert.Equal(SyncState.Live, viewModel.SyncState);
        Assert.Contains("已恢复监听", viewModel.Log.Latest?.Text);
    }

    [Fact]
    public async Task RefreshWhileRunningAsksMonitorToRetryInsteadOfRestarting()
    {
        var (monitor, _, viewModel) = Create();
        await viewModel.InitializeAsync();

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(1, monitor.RefreshCount);
    }

    [Fact]
    public async Task StartFailureIsReportedAndRefreshRetriesStart()
    {
        var (monitor, _, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        monitor.StartFailure = new AudioControlException(AudioErrorCode.ApiFailure, "启动实时音频监听", "启动实时音频监听失败。");

        await viewModel.InitializeAsync();
        Assert.Equal(SyncState.Offline, viewModel.SyncState);
        Assert.False(viewModel.HasMasterVolume);
        Assert.True(viewModel.Log.Latest?.IsError);

        monitor.StartFailure = null;
        await viewModel.RefreshCommand.ExecuteAsync();
        Assert.Equal(2, monitor.StartCount);
        Assert.Equal(SyncState.Live, viewModel.SyncState);
        Assert.Single(viewModel.Sessions);
    }

    [Fact]
    public async Task StoppedNotificationTurnsSyncOffline()
    {
        var (monitor, _, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();

        monitor.Stop();
        monitor.Push(AudioStateChangeKind.Stopped, AudioChangeOrigin.System);

        Assert.Equal(SyncState.Offline, viewModel.SyncState);
        Assert.False(viewModel.HasMasterVolume);
    }

    [Fact]
    public async Task MuteCommandsChangeOnlyTheirTarget()
    {
        var (_, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f), Session(DeviceA, "b", "Beta", 0.5f));
        await viewModel.InitializeAsync();

        viewModel.Sessions[0].ToggleMuteCommand.Execute(null);
        viewModel.ToggleMasterMuteCommand.Execute(null);

        Assert.True(viewModel.Sessions[0].IsMuted);
        Assert.False(viewModel.Sessions[1].IsMuted);
        Assert.True(viewModel.IsMasterMuted);
        Assert.Equal(new[] { (viewModel.Sessions[0].Key, true) }, controller.MuteWrites);
        Assert.Equal(new[] { (DeviceA, true) }, controller.MasterMuteWrites);
    }

    [Fact]
    public async Task ExpiredSessionIsRemovedWhenControlFails()
    {
        var (_, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();
        controller.FailSessionWrites = new AudioControlException(AudioErrorCode.SessionUnavailable, "设置会话音量", "会话不存在。");

        viewModel.Sessions[0].Volume = 70;

        Assert.Empty(viewModel.Sessions);
        Assert.Contains("已从列表移除", viewModel.Log.Latest?.Text);
    }

    [Fact]
    public async Task FailedWriteIsReportedAndSliderReloadsActualValue()
    {
        var (_, controller, viewModel) = Create(Session(DeviceA, "a", "Alpha", 0.5f));
        await viewModel.InitializeAsync();
        controller.FailSessionWrites = new AudioControlException(AudioErrorCode.ApiFailure, "设置会话音量", "设置会话音量失败。");

        viewModel.Sessions[0].Volume = 70;

        Assert.Equal(50, viewModel.Sessions[0].Volume);
        Assert.True(viewModel.Log.Latest?.IsError);
    }

    [Fact]
    public async Task UnavailableDeviceIsExplainedAndNotControlled()
    {
        var (monitor, controller, viewModel) = Create();
        monitor.AddDevice(DeviceB, "Unplugged headset", AudioDeviceState.Unplugged);
        await viewModel.InitializeAsync();

        viewModel.ShowUnavailableDevices = true;
        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == DeviceB);

        Assert.True(viewModel.HasMasterVolume);
        Assert.True(viewModel.IsDeviceWarning);
        Assert.Contains("未插入", viewModel.DeviceStatusText);
        Assert.Equal(DeviceA, viewModel.SelectedDevice?.Id);
        Assert.Empty(controller.DefaultWrites);
    }

    private static (FakeMonitor Monitor, FakeController Controller, MainViewModel ViewModel) Create(params AudioSession[] sessions)
    {
        // xUnit 的同步上下文会把通知循环的续体异步投递；去掉后假监听推送的通知在 Push 返回前即被处理，测试可确定。
        SynchronizationContext.SetSynchronizationContext(null);
        var monitor = new FakeMonitor();
        monitor.AddDevice(DeviceA, "Speakers", sessions: sessions);
        monitor.SetDefault(DeviceA);
        var controller = new FakeController(monitor);
        return (monitor, controller, new MainViewModel(controller, monitor, new AppIconProvider(), new OperationLog(), TimeProvider.System, defaultOutput: controller));
    }

    private static AudioSession Session(string device, string instance, string name, float level, AudioSessionState state = AudioSessionState.Active,
        bool isMuted = false, string? path = null, uint? pid = null)
        => new(new AudioSessionKey(device, instance), instance, pid ?? instance[0], name, path, false, state, new AudioVolume(level, isMuted));

    private sealed class FakeMonitor : IAudioStateMonitor
    {
        private readonly Dictionary<string, DeviceData> devices = new(StringComparer.Ordinal);
        private readonly Queue<AudioStateChanged> pending = new();
        private TaskCompletionSource? signal;
        private string? defaultId;
        private bool running;
        private long revision;

        public int StartCount { get; private set; }
        public int RefreshCount { get; private set; }
        public Exception? StartFailure { get; set; }

        public void AddDevice(string id, string name, AudioDeviceState state = AudioDeviceState.Active, IEnumerable<AudioSession>? sessions = null)
            => devices[id] = new DeviceData(new AudioOutputDevice(id, name, state))
            {
                Sessions = (sessions ?? []).ToDictionary(session => session.Key)
            };

        public void SetDefault(string id) => defaultId = id;
        public void SetMaster(string id, AudioVolume volume) => devices[id].Master = volume;
        public void SetListening(string id, bool listening) => devices[id].Listening = listening;
        public void SetSession(AudioSession session) => devices[session.Key.DeviceId].Sessions[session.Key] = session;
        public void RemoveSession(AudioSessionKey key) => devices[key.DeviceId].Sessions.Remove(key);
        public void Stop() => running = false;
        public AudioSession GetSession(AudioSessionKey key) => devices[key.DeviceId].Sessions[key];
        public AudioVolume GetMaster(string id) => devices[id].Master;

        public void Push(AudioStateChangeKind kind, AudioChangeOrigin origin, string? deviceId = null, AudioSessionKey? key = null, string? message = null)
        {
            pending.Enqueue(new AudioStateChanged(kind, origin, Snapshot(), deviceId, key, Message: message));
            signal?.TrySetResult();
        }

        public Task<AudioRuntimeState> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (StartFailure is { } failure) return Task.FromException<AudioRuntimeState>(failure);
            running = true;
            return Task.FromResult(Snapshot());
        }

        public Task StopAsync(CancellationToken cancellationToken = default) { running = false; return Task.CompletedTask; }
        public Task<AudioRuntimeState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());

        public Task<AudioRuntimeState> RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.FromResult(Snapshot());
        }

        public async IAsyncEnumerable<AudioStateChanged> ReadChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                while (pending.TryDequeue(out var change)) yield return change;
                signal = new TaskCompletionSource();
                using (cancellationToken.Register(() => signal.TrySetCanceled()))
                    await signal.Task;
            }
        }

        private AudioRuntimeState Snapshot()
            => new(++revision, DateTimeOffset.Now, running,
                devices.Values.Select(data => new AudioDeviceRuntimeState(data.Device,
                    running && data.Listening && data.Device.State == AudioDeviceState.Active ? data.Master : null,
                    running && data.Listening && data.Device.State == AudioDeviceState.Active,
                    running && data.Listening ? data.Sessions.Values.Where(session => session.State != AudioSessionState.Expired).ToList() : [],
                    [])).ToList(),
                new Dictionary<AudioDeviceRole, string?>
                {
                    [AudioDeviceRole.Console] = defaultId,
                    [AudioDeviceRole.Multimedia] = defaultId,
                    [AudioDeviceRole.Communications] = defaultId
                },
                []);

        public void Dispose() => signal?.TrySetCanceled();

        private sealed class DeviceData(AudioOutputDevice device)
        {
            public AudioOutputDevice Device { get; } = device;
            public AudioVolume Master { get; set; } = new(0.5f, false);
            public bool Listening { get; set; } = true;
            public Dictionary<AudioSessionKey, AudioSession> Sessions { get; set; } = [];
        }
    }

    /// <summary>只负责写入；写入同步修改假监听的状态，但不自动推送通知，由测试显式模拟系统回传。</summary>
    private sealed class FakeController(FakeMonitor monitor) : IAudioController, IDefaultAudioOutputController
    {
        private readonly Queue<TaskCompletionSource> heldWrites = new();
        public List<string> DefaultWrites { get; } = [];
        public TaskCompletionSource? DefaultGate { get; set; }
        public Exception? DefaultFailure { get; set; }
        public bool IgnoreDefaultWrites { get; set; }
        public async Task SetDefaultOutputDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            DefaultWrites.Add(deviceId);
            if (DefaultGate is { } pending) await pending.Task.WaitAsync(cancellationToken);
            if (DefaultFailure is { } failure) throw failure;
            if (!IgnoreDefaultWrites) monitor.SetDefault(deviceId);
        }

        public List<(AudioSessionKey Key, float Level)> Writes { get; } = [];
        public List<(AudioSessionKey Key, bool Muted)> MuteWrites { get; } = [];
        public List<(string DeviceId, float Level)> MasterWrites { get; } = [];
        public List<(string DeviceId, bool Muted)> MasterMuteWrites { get; } = [];
        public bool HoldWrites { get; set; }
        public Exception? FailSessionWrites { get; set; }
        public Exception? FailMasterWrites { get; set; }

        public void ReleaseNextWrite() => heldWrites.Dequeue().SetResult();

        public Task<AudioEnumeration<AudioOutputDevice>> EnumerateOutputDevicesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("界面读取应来自实时监听。");

        public Task<AudioOutputDevice?> GetDefaultOutputDeviceAsync(AudioDeviceRole role = AudioDeviceRole.Multimedia, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("界面读取应来自实时监听。");

        public Task<AudioEnumeration<AudioSession>> EnumerateSessionsAsync(string deviceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("界面读取应来自实时监听。");

        public Task<AudioVolume> GetMasterVolumeAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(monitor.GetMaster(deviceId));

        public Task<AudioVolume> SetMasterVolumeAsync(string deviceId, float level, CancellationToken cancellationToken = default)
        {
            MasterWrites.Add((deviceId, level));
            if (FailMasterWrites is { } failure) return Task.FromException<AudioVolume>(failure);
            var result = new AudioVolume(level, monitor.GetMaster(deviceId).IsMuted);
            monitor.SetMaster(deviceId, result);
            return Task.FromResult(result);
        }

        public Task<AudioVolume> SetMasterMuteAsync(string deviceId, bool isMuted, CancellationToken cancellationToken = default)
        {
            MasterMuteWrites.Add((deviceId, isMuted));
            if (FailMasterWrites is { } failure) return Task.FromException<AudioVolume>(failure);
            var result = new AudioVolume(monitor.GetMaster(deviceId).Level, isMuted);
            monitor.SetMaster(deviceId, result);
            return Task.FromResult(result);
        }

        public Task<AudioVolume> GetSessionVolumeAsync(AudioSessionKey key, CancellationToken cancellationToken = default)
            => Task.FromResult(monitor.GetSession(key).Volume);

        public async Task<AudioVolume> SetSessionVolumeAsync(AudioSessionKey key, float level, CancellationToken cancellationToken = default)
        {
            Writes.Add((key, level));
            if (FailSessionWrites is { } failure) throw failure;
            if (HoldWrites)
            {
                var hold = new TaskCompletionSource();
                heldWrites.Enqueue(hold);
                await hold.Task;
            }
            var session = monitor.GetSession(key);
            var result = new AudioVolume(level, session.Volume.IsMuted);
            monitor.SetSession(session with { Volume = result });
            return result;
        }

        public Task<AudioVolume> SetSessionMuteAsync(AudioSessionKey key, bool isMuted, CancellationToken cancellationToken = default)
        {
            MuteWrites.Add((key, isMuted));
            var session = monitor.GetSession(key);
            var result = new AudioVolume(session.Volume.Level, isMuted);
            monitor.SetSession(session with { Volume = result });
            return Task.FromResult(result);
        }

        public void Dispose() { }
    }
}
