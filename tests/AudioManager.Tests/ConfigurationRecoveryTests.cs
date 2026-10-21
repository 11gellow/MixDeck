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
        var savedJson = ConfigurationJson.Serialize(scenario.Service.Current);
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false), ProcessId = 999 };
        var unrelated = scenario.Raw.AddSession("other", .91f, false, @"C:\Other\Player.exe");
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Empty(scenario.Raw.Writes);
        await scenario.Service.RestoreSnapshotAsync(scenario.State(), scenario.Service.Current.ActiveProfileId!.Value);
        Assert.Equal(new AudioVolume(.37f, true), scenario.Raw.Sessions[session.Key].Volume);
        Assert.Equal(.91f, scenario.Raw.Sessions[unrelated.Key].Volume.Level);
        Assert.Equal(savedJson, ConfigurationJson.Serialize(scenario.Service.Current));
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
        await scenario.EnableAsync();
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

    [Fact]
    public async Task ExternalDriftIsCorrectedSelfEchoIgnoredAndDisablingStopsAutomaticWrites()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .31f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        await scenario.EnableAsync();
        await scenario.Service.HandleStateAsync(scenario.State());
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        Assert.Equal(.31f, scenario.Raw.Sessions[session.Key].Volume.Level);
        var count = scenario.Raw.Writes.Count;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        Assert.Equal(count, scenario.Raw.Writes.Count);
        await scenario.Service.UpdateSettingsAsync(scenario.Service.Current.Settings with { AutomaticRestoreEnabled = false });
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.6f, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        Assert.Equal(.6f, scenario.Raw.Sessions[session.Key].Volume.Level);
        Assert.Equal(count, scenario.Raw.Writes.Count);
    }

    [Fact]
    public async Task LateSessionAndDeviceReconnectionApplyPendingTargetsAndOnlyOriginalDeviceIsMatched()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("old", .29f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        scenario.Raw.Sessions.Clear();
        await scenario.EnableAsync();
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
        await scenario.Service.UpdateRulesAsync(first.Id, first.Rules.Select(rule => rule.Target.Target.Application is null ? rule
            : rule with { Target = rule.Target with { Volume = new(.2f, true) } }).ToArray());
        Assert.Equal(.3f, scenario.Service.Current.Profiles[0].Snapshot.Entries.Single(entry => entry.Target.Application is not null).Volume.Level);
        await scenario.Service.HandleStateAsync(scenario.State());
        await scenario.EnableAsync();
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
        await scenario.EnableAsync();
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.9f, false) };
        scenario.Raw.IgnoreWrites = ignoreWrite;
        scenario.Raw.Failure = ignoreWrite ? null : new UnauthorizedAccessException("系统限制");
        await scenario.Service.HandleStateAsync(scenario.State());
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
    public async Task ImportDisablesAutomaticWritesAndResetDoesNotChangeSystemVolume()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var sample = ConfigurationStoreTests.Sample();
        var import = Path.Combine(scenario.Directory, "import.json");
        await File.WriteAllTextAsync(import, ConfigurationJson.Serialize(sample));
        await scenario.Service.ImportAsync(import);
        Assert.False(scenario.Service.Current.Settings.AutomaticRestoreEnabled);
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
        await scenario.Service.UpdateRulesAsync(profile.Id, profile.Rules.Select(rule => rule with { Enabled = false }).ToArray());
        scenario.Raw.Sessions[system.Key] = system with { Volume = new(.8f, false) };
        await scenario.EnableAsync();
        await scenario.Service.HandleStateAsync(scenario.State());
        Assert.Empty(scenario.Raw.Writes);
    }

    [Fact]
    public async Task DelayedSelfSnapshotReadsCurrentSystemValueWithoutWriteLoopAndManualSnapshotCannotFightActiveRules()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .25f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "方案");
        await scenario.EnableAsync();
        var stale = scenario.State();
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false) };
        await scenario.Service.HandleStateAsync(stale, new(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, stale, "device-a", session.Key));
        Assert.Equal(.25f, scenario.Raw.Sessions[session.Key].Volume.Level);
        var writes = scenario.Raw.Writes.Count;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        Assert.Equal(writes, scenario.Raw.Writes.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.RestoreSnapshotAsync(scenario.State(), scenario.Service.Current.ActiveProfileId!.Value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.UpdateRulesAsync(Guid.NewGuid(), []));
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
            Listening, Listening ? Raw.Sessions.Values.ToArray() : [], [])], new Dictionary<AudioDeviceRole, string?> { [AudioDeviceRole.Multimedia] = "device-a" }, []);
    public Task EnableAsync() => Service.UpdateSettingsAsync(Service.Current.Settings with { AutomaticRestoreEnabled = true });
    public Task PushAsync(AudioStateChangeKind kind, AudioChangeOrigin origin, AudioSessionKey? sessionKey = null)
    {
        var state = State();
        return Service.HandleStateAsync(state, new(kind, origin, state, "device-a", sessionKey));
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
    public AudioVolume Master { get; set; } = new(.5f, false);
    public bool IgnoreWrites { get; set; }
    public Exception? Failure { get; set; }
    public AudioSession AddSession(string instance, float level, bool muted = false, string? path = @"C:\Apps\Player.exe", uint pid = 100)
    {
        var session = new AudioSession(new("device-a", instance), "volatile-" + instance, pid, "Player", path, false, AudioSessionState.Active, new(level, muted));
        Sessions[session.Key] = session;
        return session;
    }
    public Task<AudioVolume> GetMasterVolumeAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(Master);
    public Task<AudioVolume> GetSessionVolumeAsync(AudioSessionKey key, CancellationToken cancellationToken = default) => Task.FromResult(Sessions[key].Volume);
    public Task<AudioVolume> SetMasterVolumeAsync(string deviceId, float level, CancellationToken cancellationToken = default)
        => Write(deviceId, () => { if (!IgnoreWrites) Master = new(level, Master.IsMuted); return Master; });
    public Task<AudioVolume> SetMasterMuteAsync(string deviceId, bool isMuted, CancellationToken cancellationToken = default)
        => Write(deviceId, () => { if (!IgnoreWrites) Master = new(Master.Level, isMuted); return Master; });
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
