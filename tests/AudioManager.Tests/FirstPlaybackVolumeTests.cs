using System.IO;
using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Models;

namespace AudioManager.Tests;

public sealed class FirstPlaybackVolumeTests
{
    [Fact]
    public async Task FirstPlaybackTraceKeepsTargetAndRestoresOnlyResetSessionWithoutLearningEcho()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = AddInactive(scenario, .096262924f);
        var peer = scenario.Raw.AddSession("peer", .096262924f);
        var other = scenario.Raw.AddSession("other", .42f, path: @"C:\Other\Player.exe");
        await scenario.StartAsync();
        var file = await File.ReadAllTextAsync(scenario.Store.FilePath);
        scenario.Raw.Writes.Clear();
        // 真实现场顺序：同一空闲会话 GUID_NULL → 100%，随后才 Active。
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(1, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key, Guid.Empty);
        Assert.Equal(session.Volume, scenario.Raw.Sessions[session.Key].Volume);
        Assert.Equal(file, await File.ReadAllTextAsync(scenario.Store.FilePath));
        Assert.Single(scenario.Raw.Writes);
        Assert.Equal(peer.Volume, scenario.Raw.Sessions[peer.Key].Volume);
        Assert.Equal(other.Volume, scenario.Raw.Sessions[other.Key].Volume);
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        scenario.Raw.Sessions[session.Key] = scenario.Raw.Sessions[session.Key] with { State = AudioSessionState.Active };
        await scenario.PushAsync(AudioStateChangeKind.SessionStateChanged, AudioChangeOrigin.System, session.Key);
        Assert.Equal(file, await File.ReadAllTextAsync(scenario.Store.FilePath));
        Assert.Single(scenario.Raw.Writes);
    }

    [Theory]
    [InlineData(false, false, false)] // 正常带来源的外部调节。
    [InlineData(true, true, false)] // 已播放会话的空来源调节。
    [InlineData(true, false, true)] // 音量和静音同时修改，不是现场重置模式。
    public async Task OrdinaryExternalChangesStillPersist(bool emptyContext, bool played, bool muted)
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = AddInactive(scenario, .1f);
        await scenario.StartAsync();
        if (played)
        {
            scenario.Raw.Sessions[session.Key] = session with { State = AudioSessionState.Active };
            await scenario.PushAsync(AudioStateChangeKind.SessionStateChanged, AudioChangeOrigin.System, session.Key);
            scenario.Raw.Sessions[session.Key] = session;
            await scenario.PushAsync(AudioStateChangeKind.SessionStateChanged, AudioChangeOrigin.System, session.Key);
        }
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(1, muted) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key,
            emptyContext ? Guid.Empty : Guid.NewGuid());
        Assert.Equal(new SavedVolume(1, muted), ApplicationRule(scenario).Target.Volume);
        Assert.Empty(scenario.Raw.Writes);
    }

    [Fact]
    public async Task ProtectionIsOneAttemptAndDoesNotRetryRejectedWrites()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = AddInactive(scenario, .1f);
        await scenario.StartAsync();
        scenario.Raw.IgnoreWrites = true;
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(1, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key, Guid.Empty);
        var attempts = scenario.Raw.Writes.Count;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key, Guid.Empty);
        Assert.Equal(1, attempts);
        Assert.Equal(attempts, scenario.Raw.Writes.Count);
        Assert.Equal(.1f, ApplicationRule(scenario).Target.Volume.Level);
        Assert.Contains(scenario.Reports, report => !report.Succeeded && report.Message.Contains("停止自动重试"));
    }

    [Fact]
    public async Task SecondResetIsNotContinuouslyOverwrittenAndNewSessionGetsItsOwnBudget()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = AddInactive(scenario, .1f);
        await scenario.StartAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            scenario.Raw.Sessions[session.Key] = session with { Volume = new(1, false) };
            await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key, Guid.Empty);
        }
        Assert.Single(scenario.Raw.Writes);
        Assert.Equal(1, ApplicationRule(scenario).Target.Volume.Level);
        scenario.Raw.Sessions.Remove(session.Key);
        await scenario.PushAsync(AudioStateChangeKind.SessionRemoved, AudioChangeOrigin.System, session.Key);
        var profile = scenario.Service.ProfileForDevice("device-a")!;
        await scenario.Service.UpdateRuleAsync(profile.Id, "device-a", ApplicationRule(scenario).Id, level: .1f);
        scenario.Raw.Sessions[session.Key] = session;
        await scenario.PushAsync(AudioStateChangeKind.SessionCreated, AudioChangeOrigin.System, session.Key);
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(1, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key, Guid.Empty);
        Assert.Equal(.1f, scenario.Raw.Sessions[session.Key].Volume.Level);
    }

    [Theory]
    [InlineData(false, .7f)]
    [InlineData(true, 1f)]
    public async Task OtherLevelsAndDisabledRulesKeepExistingSaveBehavior(bool disabled, float level)
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = AddInactive(scenario, .1f);
        await scenario.StartAsync();
        if (disabled)
        {
            var profile = scenario.Service.ProfileForDevice("device-a")!;
            await scenario.Service.UpdateRuleAsync(profile.Id, "device-a", ApplicationRule(scenario).Id, enabled: false);
        }
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(level, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key, Guid.Empty);
        Assert.Equal(level, ApplicationRule(scenario).Target.Volume.Level);
        Assert.Empty(scenario.Raw.Writes);
    }

    private static RestoreRule ApplicationRule(ConfigurationScenario scenario)
        => scenario.Service.ProfileForDevice("device-a")!.Rules.Single(rule => rule.Target.Target.Application is not null);

    private static AudioSession AddInactive(ConfigurationScenario scenario, float level)
    {
        var session = scenario.Raw.AddSession("player", level) with { State = AudioSessionState.Inactive };
        scenario.Raw.Sessions[session.Key] = session;
        return session;
    }
}
