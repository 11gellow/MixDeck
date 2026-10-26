using System.IO;
using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Core.Services;

namespace AudioManager.Tests;

public sealed class ConfigurationRecoveryTests
{
    [Fact]
    public async Task SavedSnapshotDoesNotFollowLiveValuesAndManualRestoreUsesOnlyMatchingDevicesAndApplications()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("first", .37f, true);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "工作");
        var originalSnapshot = scenario.Service.Current.Profiles[0].Snapshot;
        await scenario.Service.HandleStateAsync(scenario.State());
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false), ProcessId = 999 };
        var unrelated = scenario.Raw.AddSession("other", .91f, false, @"C:\Other\Player.exe");
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Empty(scenario.Raw.Writes);
        await scenario.Service.RestoreSnapshotAsync(scenario.State(), scenario.Service.Current.ActiveProfileId!.Value);
        Assert.Equal(new AudioVolume(.37f, true), scenario.Raw.Sessions[session.Key].Volume);
        Assert.Equal(.91f, scenario.Raw.Sessions[unrelated.Key].Volume.Level);
        Assert.Equal(originalSnapshot, scenario.Service.Current.Profiles[0].Snapshot);
        Assert.Contains(scenario.Reports, report => report.Succeeded);
    }

    [Fact]
    public async Task RestartedPidAndInstanceAndAllSessionsOfSamePathRestoreTogetherWhileNewPathIsNotMatched()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("old", .24f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        scenario.Raw.Sessions.Clear();
        var first = scenario.Raw.AddSession("new-1", .7f, path: @"c:\apps\PLAYER.exe", pid: 200);
        var second = scenario.Raw.AddSession("new-2", .8f, pid: 201);
        var other = scenario.Raw.AddSession("same-name", .9f, path: @"D:\Other\Player.exe", pid: 202);
        await scenario.StartAsync();
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Equal(.24f, scenario.Raw.Sessions[first.Key].Volume.Level);
        Assert.Equal(.24f, scenario.Raw.Sessions[second.Key].Volume.Level);
        Assert.Equal(.9f, scenario.Raw.Sessions[other.Key].Volume.Level);
    }

    [Fact]
    public async Task ConflictingMultiSessionSaveIsRejectedAtomicallyAndUnreadableIdentityIsReportedNotGuessed()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("a", .2f);
        scenario.Raw.AddSession("b", .8f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.SaveSnapshotAsync(scenario.State(), "冲突"));
        Assert.Empty(scenario.Service.Current.Profiles);
        Assert.False(File.Exists(scenario.Store.FilePath));
        scenario.Raw.Sessions.Clear();
        scenario.Raw.AddSession("unreadable", .2f, path: null);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "可读部分");
        Assert.Empty(scenario.Service.Current.KnownApplications);
        Assert.Contains(scenario.Reports, report => !report.Succeeded && report.Message.Contains("路径不可读"));
        Assert.Single(scenario.Service.Current.Profiles[0].Snapshot.Entries); // 只有主音量。
    }

    [Theory]
    [InlineData(AudioChangeOrigin.External)]
    [InlineData(AudioChangeOrigin.Self)]
    public async Task UserChangePersistsNewTargetUnifiesPeersAndKeepsSnapshotWithoutWriteLoop(AudioChangeOrigin origin)
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .31f);
        var peer = scenario.Raw.AddSession("b", .31f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        await scenario.StartAsync();
        var snapshot = scenario.Service.Current.Profiles[0].Snapshot;
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, true) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, origin, session.Key);
        Assert.Equal(new AudioVolume(.8f, true), scenario.Raw.Sessions[session.Key].Volume);
        Assert.Equal(new AudioVolume(.8f, true), scenario.Raw.Sessions[peer.Key].Volume);
        Assert.Equal(new SavedVolume(.8f, true), scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume);
        Assert.Equal(snapshot, scenario.Service.Current.Profiles[0].Snapshot);
        var json = await File.ReadAllTextAsync(scenario.Store.FilePath);
        var count = scenario.Raw.Writes.Count;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, peer.Key);
        Assert.Equal(count, scenario.Raw.Writes.Count);
        Assert.Equal(json, await File.ReadAllTextAsync(scenario.Store.FilePath));
        using var reloaded = new AudioConfigurationService(new JsonConfigurationStore(scenario.Store.FilePath), scenario.Guard);
        await reloaded.InitializeAsync();
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.1f, false) };
        await reloaded.HandleStateAsync(scenario.State());
        Assert.Equal(new AudioVolume(.8f, true), scenario.Raw.Sessions[session.Key].Volume);
    }

    [Fact]
    public async Task LateSessionAndDeviceReconnectionApplyPendingTargetsAndOnlyOriginalDeviceIsMatched()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("old", .29f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        scenario.Raw.Sessions.Clear();
        await scenario.StartAsync();
        await scenario.Service.HandleStateAsync(scenario.State());
        var late = scenario.Raw.AddSession("late", 1);
        await scenario.PushAsync(AudioStateChangeKind.SessionCreated, AudioChangeOrigin.System, late.Key);
        Assert.Equal(.29f, scenario.Raw.Sessions[late.Key].Volume.Level);
        scenario.Listening = false;
        await scenario.PushAsync(AudioStateChangeKind.DeviceListeningChanged, AudioChangeOrigin.System);
        scenario.Raw.Sessions[late.Key] = late with { Volume = new(.9f, false) };
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Equal(.9f, scenario.Raw.Sessions[late.Key].Volume.Level);
        scenario.Listening = true;
        await scenario.PushAsync(AudioStateChangeKind.DeviceListeningChanged, AudioChangeOrigin.Recovery);
        Assert.Equal(.29f, scenario.Raw.Sessions[late.Key].Volume.Level);
        Assert.All(scenario.Raw.Writes, write => Assert.Contains("device-a", write));
    }

    [Fact]
    public async Task RulesCanBeEditedWithoutChangingSnapshotAndSelectingAnotherProfileChangesTargets()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .3f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "一");
        var first = scenario.Service.Current.Profiles[0];
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.7f, false) };
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "二");
        var second = scenario.Service.Current.Profiles[1];
        Assert.Equal(second.Id, scenario.Service.Current.ActiveProfileId);
        await scenario.Service.UpdateRuleAsync(first.Id, "device-a", first.Rules.Single(rule => rule.Target.Target.Application is not null).Id, level: .2f, muted: true);
        Assert.Equal(.3f, scenario.Service.Current.Profiles[0].Snapshot.Entries.Single(entry => entry.Target.Application is not null).Volume.Level);
        await scenario.Service.HandleStateAsync(scenario.State());
        await scenario.StartAsync();
        await scenario.Service.SelectProfileAsync(first.Id);
        Assert.Equal(new AudioVolume(.2f, true), scenario.Raw.Sessions[session.Key].Volume);
        await scenario.Service.DeleteProfileAsync(first.Id);
        Assert.Null(scenario.Service.Current.ActiveProfileId);
        scenario.Raw.Sessions[session.Key] = session;
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Equal(session.Volume, scenario.Raw.Sessions[session.Key].Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrIneffectiveWriteShowsActualRestrictionAndStopsRetryUntilExplicitRefresh(bool ignoreWrite)
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .32f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        await scenario.StartAsync();
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.9f, false) };
        scenario.Raw.IgnoreWrites = ignoreWrite;
        scenario.Raw.Failure = ignoreWrite ? null : new UnauthorizedAccessException("系统限制");
        await scenario.Service.HandleStateAsync(scenario.State(), retry: true);
        Assert.NotNull(scenario.Guard.Restriction(AudioWriteGuard.SessionKey(session.Key)));
        Assert.Equal(.9f, (await scenario.Guard.GetSessionVolumeAsync(session.Key)).Level);
        Assert.Contains(scenario.Reports, report => !report.Succeeded && report.Message.Contains("实际"));
        var count = scenario.Raw.Writes.Count;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        Assert.Equal(count, scenario.Raw.Writes.Count);
        scenario.Raw.IgnoreWrites = false;
        scenario.Raw.Failure = null;
        scenario.Guard.ClearRestrictions();
        await scenario.Service.HandleStateAsync(scenario.State(), retry: true);
        Assert.Equal(.32f, scenario.Raw.Sessions[session.Key].Volume.Level);
        Assert.Null(scenario.Guard.Restriction(AudioWriteGuard.SessionKey(session.Key)));
    }

    [Fact]
    public async Task ImportPreservesLocalLoginPreferenceAndResetDoesNotChangeSystemVolume()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var sample = ConfigurationStoreTests.Sample();
        var import = Path.Combine(scenario.Directory, "import.json");
        await File.WriteAllTextAsync(import, ConfigurationJson.Serialize(sample));
        await scenario.Service.ImportAsync(import);
        Assert.False(scenario.Service.Current.Settings.StartAtLogin);
        await scenario.Service.ResetAsync();
        Assert.Empty(scenario.Service.Current.Profiles);
        Assert.Empty(scenario.Raw.Writes);
    }

    [Fact]
    public async Task SystemSoundsUseSpecialIdentityAndDisabledRuleDoesNotWrite()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var system = scenario.Raw.AddSession("system", .35f, path: null) with { IsSystemSounds = true };
        scenario.Raw.Sessions[system.Key] = system;
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "系统");
        Assert.Equal(ApplicationIdentityKind.SystemSounds, Assert.Single(scenario.Service.Current.KnownApplications).Kind);
        var profile = scenario.Service.Current.Profiles[0];
        foreach (var rule in profile.Rules) await scenario.Service.UpdateRuleAsync(profile.Id, "device-a", rule.Id, enabled: false);
        scenario.Raw.Sessions[system.Key] = system with { Volume = new(.8f, false) };
        await scenario.StartAsync();
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Empty(scenario.Raw.Writes);
    }

    [Fact]
    public async Task DelayedSelfSnapshotSavesCurrentReadbackAndManualSnapshotBecomesCurrentTarget()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .25f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        await scenario.StartAsync();
        var stale = scenario.State();
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false) };
        await scenario.Service.HandleStateAsync(stale, new(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, stale, "device-a", session.Key));
        Assert.Equal(.8f, scenario.Raw.Sessions[session.Key].Volume.Level);
        var writes = scenario.Raw.Writes.Count;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        Assert.Equal(writes, scenario.Raw.Writes.Count);
        await scenario.Service.RestoreSnapshotAsync(scenario.State(), scenario.Service.Current.ActiveProfileId!.Value);
        Assert.Equal(.25f, scenario.Raw.Sessions[session.Key].Volume.Level);
        Assert.Equal(.25f, scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.UpdateRuleAsync(Guid.NewGuid(), "device-a", Guid.NewGuid(), level: .2f));
    }

    [Fact]
    public async Task ReloadingPersistedServiceAndOfflineManualRestoreKeepsMissingTargetsForLater()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("a", .3f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "保存");
        using var reloaded = new AudioConfigurationService(new JsonConfigurationStore(scenario.Store.FilePath), scenario.Guard);
        var reports = new List<RestoreResult>();
        reloaded.Reported += reports.Add;
        await reloaded.InitializeAsync();
        Assert.Equal("保存", reloaded.Current.Profiles[0].Name);
        scenario.Listening = false;
        await reloaded.RestoreSnapshotAsync(scenario.State(), reloaded.Current.ActiveProfileId!.Value);
        Assert.All(reports, report => Assert.False(report.Succeeded));
        Assert.Equal(2, reports.Count);
        Assert.Empty(scenario.Raw.Writes);
        Assert.Equal(2, reloaded.Current.Profiles[0].Snapshot.Entries.Count);
    }
    [Fact]
    public async Task DeviceProfilesSwitchWithDefaultAndPersistIndependentMasterAndAppTargets()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.OtherDevices.Add("device-b");
        scenario.Raw.Masters["device-a"] = new(.2f, false);
        scenario.Raw.Masters["device-b"] = new(.8f, true);
        var first = scenario.Raw.AddSession("a", .3f);
        var second = scenario.Raw.AddSession("b", .7f, deviceId: "device-b");
        await scenario.Service.HandleStateAsync(scenario.State());
        var a = scenario.Service.ProfileForDevice("device-a")!;
        var b = scenario.Service.ProfileForDevice("device-b")!;
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, scenario.Service.Current.ActiveProfileId);
        scenario.Raw.Masters["device-b"] = new(.66f, false);
        var state = scenario.State();
        await scenario.Service.HandleStateAsync(state, new(AudioStateChangeKind.MasterVolumeChanged, AudioChangeOrigin.External, state, "device-b"));
        scenario.Raw.Sessions[second.Key] = second with { Volume = new(.62f, true) };
        state = scenario.State();
        await scenario.Service.HandleStateAsync(state, new(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, state, "device-b", second.Key));
        scenario.Raw.Masters["device-b"] = new(.1f, true);
        scenario.Raw.Sessions[second.Key] = second;
        scenario.DefaultDeviceId = "device-b";
        await scenario.PushAsync(AudioStateChangeKind.DefaultDeviceChanged, AudioChangeOrigin.System);
        Assert.Equal(b.Id, scenario.Service.Current.ActiveProfileId);
        Assert.Equal(new AudioVolume(.66f, false), scenario.Raw.Masters["device-b"]);
        Assert.Equal(new AudioVolume(.62f, true), scenario.Raw.Sessions[second.Key].Volume);
        Assert.Equal(new AudioVolume(.2f, false), scenario.Raw.Master);
        Assert.Equal(first.Volume, scenario.Raw.Sessions[first.Key].Volume);
        scenario.DefaultDeviceId = "device-a";
        await scenario.PushAsync(AudioStateChangeKind.DefaultDeviceChanged, AudioChangeOrigin.System);
        Assert.Equal(a.Id, scenario.Service.Current.ActiveProfileId);
        var loaded = (await new JsonConfigurationStore(scenario.Store.FilePath).LoadAsync()).Configuration;
        Assert.Equal(2, loaded.DeviceProfiles.Count);
        Assert.Equal(.66f, loaded.Profiles.Single(profile => profile.Id == b.Id).Rules.Single(rule => rule.Target.Target.Application is null).Target.Volume.Level);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.SelectProfileAsync(b.Id, "device-a"));
    }

    [Fact]
    public async Task FirstRunRemembersDeviceAutomaticallyAndUnknownLateApplicationIsLearned()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Single(scenario.Service.Current.Profiles);
        var late = scenario.Raw.AddSession("new", .42f, true);
        await scenario.PushAsync(AudioStateChangeKind.SessionCreated, AudioChangeOrigin.System, late.Key);
        Assert.Equal(new SavedVolume(.42f, true), scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume);
        Assert.Empty(scenario.Raw.Writes);
    }

    [Fact]
    public async Task RestoreEchoAndRestrictionCannotReplaceSavedTargetWithFailedOrStaleValues()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .25f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.9f, true) };
        var stale = scenario.State();
        await scenario.Service.HandleStateAsync(stale);
        var saved = await File.ReadAllTextAsync(scenario.Store.FilePath);
        await scenario.Service.HandleStateAsync(stale, new(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, stale, "device-a", session.Key));
        Assert.Equal(saved, await File.ReadAllTextAsync(scenario.Store.FilePath));
        scenario.Raw.IgnoreWrites = true;
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false) };
        await scenario.Service.HandleStateAsync(scenario.State(), retry: true);
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        Assert.Equal(saved, await File.ReadAllTextAsync(scenario.Store.FilePath));
        Assert.NotNull(scenario.Guard.Restriction(AudioWriteGuard.SessionKey(session.Key)));
    }

    [Fact]
    public async Task FailedAutosaveKeepsPreviousFileAndTargetWithoutUndoingUserVolume()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .3f);
        await scenario.Service.HandleStateAsync(scenario.State());
        var before = ConfigurationJson.Serialize(scenario.Service.Current);
        await File.WriteAllTextAsync(scenario.Store.FilePath, "changed-by-other-process");
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        Assert.Equal(.8f, scenario.Raw.Sessions[session.Key].Volume.Level);
        Assert.Equal(before, ConfigurationJson.Serialize(scenario.Service.Current));
        Assert.Equal("changed-by-other-process", await File.ReadAllTextAsync(scenario.Store.FilePath));
        Assert.Contains(scenario.Reports, report => !report.Succeeded && report.Message.Contains("自动保存失败"));
    }

    [Fact]
    public async Task BlockedConfigurationDoesNotCaptureCurrentVolumeOrWriteAudio()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await File.WriteAllTextAsync(scenario.Store.FilePath, "{\"version\":999}");
        using var service = new AudioConfigurationService(new JsonConfigurationStore(scenario.Store.FilePath), scenario.Guard);
        await service.InitializeAsync();
        await service.HandleStateAsync(scenario.State());
        Assert.Empty(service.Current.Profiles);
        Assert.Empty(scenario.Raw.Writes);
        Assert.Equal("{\"version\":999}", await File.ReadAllTextAsync(scenario.Store.FilePath));
    }

    [Fact]
    public async Task ExplicitResetUnblocksAutosaveAfterInvalidConfiguration()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await File.WriteAllTextAsync(scenario.Store.FilePath, "{broken");
        using var service = new AudioConfigurationService(new JsonConfigurationStore(scenario.Store.FilePath), scenario.Guard);
        await service.InitializeAsync();
        Assert.Equal(ConfigurationLoadKind.Blocked, service.LoadResult!.Kind);
        await service.ResetAsync();
        await service.HandleStateAsync(scenario.State());
        Assert.Single(service.Current.Profiles);
        Assert.Equal(ConfigurationLoadKind.Loaded, (await new JsonConfigurationStore(scenario.Store.FilePath).LoadAsync()).Kind);
        Assert.Equal("{broken", await File.ReadAllTextAsync(Assert.Single(System.IO.Directory.GetFiles(scenario.Directory, "*.preserved-*"))));
    }

    [Fact]
    public async Task FineUserAdjustmentIsSavedEvenWithinRestoreTolerance()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .3f);
        await scenario.Service.HandleStateAsync(scenario.State());
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.303f, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        Assert.Equal(.303f, scenario.Service.ProfileForDevice("device-a")!.Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
    }

}

internal sealed class ConfigurationScenario : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "MixDeck-recovery-" + Guid.NewGuid().ToString("N"));
    public ConfigurationTestController Raw { get; } = new();
    public AudioWriteGuard Guard { get; }
    public JsonConfigurationStore Store { get; }
    public AudioConfigurationService Service { get; }
    public List<RestoreResult> Reports { get; } = [];
    public bool Listening { get; set; } = true;
    private long revision;
    public string? DefaultDeviceId { get; set; } = "device-a";
    public List<string> OtherDevices { get; } = [];
    private ConfigurationScenario()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Store = new(Path.Combine(Directory, "settings.json"));
        Guard = new(Raw);
        Service = new(Store, Guard);
        Service.Reported += Reports.Add;
    }
    public static async Task<ConfigurationScenario> CreateAsync()
    {
        var result = new ConfigurationScenario();
        await result.Service.InitializeAsync();
        return result;
    }
    public AudioRuntimeState State() => new(++revision, DateTimeOffset.UtcNow, true,
        [new(new("device-a", "耳机", Listening ? AudioDeviceState.Active : AudioDeviceState.Unplugged), Listening ? Raw.Master : null,
            Listening, Listening ? Raw.Sessions.Values.Where(session => session.Key.DeviceId == "device-a").ToArray() : [], [])
        , .. OtherDevices.Select(id => new AudioDeviceRuntimeState(new(id, id, AudioDeviceState.Active), Raw.Masters.GetValueOrDefault(id, new(.5f, false)), true,
            Raw.Sessions.Values.Where(session => session.Key.DeviceId == id).ToArray(), []))], new Dictionary<AudioDeviceRole, string?> { [AudioDeviceRole.Multimedia] = DefaultDeviceId }, []);
    public Task StartAsync() => Service.HandleStateAsync(State());
    public Task PushAsync(AudioStateChangeKind kind, AudioChangeOrigin origin, AudioSessionKey? sessionKey = null, Guid? context = null)
    {
        var state = State();
        return Service.HandleStateAsync(state, new(kind, origin, state, "device-a", sessionKey, context));
    }
    public void Dispose()
    {
        Service.Dispose();
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}

internal sealed class ConfigurationTestController : IAudioController
{
    public Dictionary<AudioSessionKey, AudioSession> Sessions { get; } = [];
    public List<string> Writes { get; } = [];
    public Dictionary<string, AudioVolume> Masters { get; } = new() { ["device-a"] = new(.5f, false) };
    public AudioVolume Master { get => Masters["device-a"]; set => Masters["device-a"] = value; }
    public bool IgnoreWrites { get; set; }
    public Exception? Failure { get; set; }
    public AudioSession AddSession(string instance, float level, bool muted = false, string? path = @"C:\Apps\Player.exe", uint pid = 100, string deviceId = "device-a")
    {
        var session = new AudioSession(new(deviceId, instance), "volatile-" + instance, pid, "Player", path, false, AudioSessionState.Active, new(level, muted));
        Sessions[session.Key] = session;
        return session;
    }
    public Task<AudioVolume> GetMasterVolumeAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(Masters.GetValueOrDefault(deviceId, new(.5f, false)));
    public Task<AudioVolume> GetSessionVolumeAsync(AudioSessionKey key, CancellationToken cancellationToken = default) => Task.FromResult(Sessions[key].Volume);
    public Task<AudioVolume> SetMasterVolumeAsync(string deviceId, float level, CancellationToken cancellationToken = default)
        => Write(deviceId, () => { var current = Masters.GetValueOrDefault(deviceId, new(.5f, false)); if (!IgnoreWrites) Masters[deviceId] = new(level, current.IsMuted); return Masters.GetValueOrDefault(deviceId, current); });
    public Task<AudioVolume> SetMasterMuteAsync(string deviceId, bool isMuted, CancellationToken cancellationToken = default)
        => Write(deviceId, () => { var current = Masters.GetValueOrDefault(deviceId, new(.5f, false)); if (!IgnoreWrites) Masters[deviceId] = new(current.Level, isMuted); return Masters.GetValueOrDefault(deviceId, current); });
    public Task<AudioVolume> SetSessionVolumeAsync(AudioSessionKey key, float level, CancellationToken cancellationToken = default)
        => Write(key.DeviceId + ":" + key.InstanceId, () => { var session = Sessions[key]; if (!IgnoreWrites) Sessions[key] = session with { Volume = new(level, session.Volume.IsMuted) }; return Sessions[key].Volume; });
    public Task<AudioVolume> SetSessionMuteAsync(AudioSessionKey key, bool isMuted, CancellationToken cancellationToken = default)
        => Write(key.DeviceId + ":" + key.InstanceId, () => { var session = Sessions[key]; if (!IgnoreWrites) Sessions[key] = session with { Volume = new(session.Volume.Level, isMuted) }; return Sessions[key].Volume; });
    private Task<AudioVolume> Write(string key, Func<AudioVolume> action)
    {
        Writes.Add(key);
        return Failure is { } failure ? Task.FromException<AudioVolume>(failure) : Task.FromResult(action());
    }
    public Task<AudioEnumeration<AudioOutputDevice>> EnumerateOutputDevicesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AudioOutputDevice?> GetDefaultOutputDeviceAsync(AudioDeviceRole role = AudioDeviceRole.Multimedia, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AudioEnumeration<AudioSession>> EnumerateSessionsAsync(string deviceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { }
}
