using System.IO;
using AudioManager.Core.Models;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Tests;

public sealed class VolumeDiagnosticsTests
{
    [Fact]
    public async Task JournalFailureIsReportedOnceWithoutStoppingListeningOrReadback()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await scenario.StartAsync();
        using var vm = new ConfigurationViewModel(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario),
            new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), new());
        var journal = new AudioManager.Desktop.Services.AudioEventJournal(Path.Combine(scenario.Store.FilePath, "invalid.jsonl"));
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), new(), TimeProvider.System, vm, audioJournal: journal);
        await main.InitializeAsync();
        Assert.Equal(SyncState.Live, main.SyncState);
        await scenario.Service.ReapplyTargetsAsync(scenario.State(), "device-a");
        Assert.Single(main.Log.Entries, entry => entry.Source == "诊断" && entry.Text.Contains("写入失败"));
        Assert.NotNull(journal.LastError);
    }

    [Fact]
    public async Task InspectionReadsFreshActualWithoutLearningOrChangingVolumes()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("player", .4f);
        await scenario.StartAsync();
        var stale = scenario.State();
        var file = await File.ReadAllTextAsync(scenario.Store.FilePath);
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.73f, true) };
        var result = await scenario.Service.InspectAsync(stale, "device-a");
        var app = result.Single(item => item.SessionKey == session.Key);
        Assert.Equal(VolumeDiagnosticState.Differs, app.State);
        Assert.Equal(new AudioVolume(.73f, true), app.Actual);
        Assert.Equal(.4f, app.Target.Level);
        Assert.Empty(scenario.Raw.Writes);
        Assert.Equal(file, await File.ReadAllTextAsync(scenario.Store.FilePath));
    }

    [Fact]
    public async Task InspectionSeparatesMissingDisabledOfflineAndFailedSessions()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("gone", .4f);
        await scenario.StartAsync();
        var state = scenario.State();
        scenario.Raw.Sessions.Remove(session.Key);
        var failed = await scenario.Service.InspectAsync(state, "device-a");
        Assert.Contains(failed, item => item.State == VolumeDiagnosticState.Unavailable && item.NativeErrorCode is not null);
        Assert.Contains(failed, item => item.State == VolumeDiagnosticState.Matches);
        var waiting = await scenario.Service.InspectAsync(scenario.State(), "device-a");
        Assert.Contains(waiting, item => item.State == VolumeDiagnosticState.WaitingForSession);
        scenario.Listening = false;
        Assert.All(await scenario.Service.InspectAsync(scenario.State(), "device-a"), item => Assert.Equal(VolumeDiagnosticState.Unavailable, item.State));
        scenario.Listening = true;
        var profile = scenario.Service.ProfileForDevice("device-a")!;
        await scenario.Service.UpdateRuleAsync(profile.Id, "device-a", profile.Rules.Single(rule => rule.Target.Target.Application is not null).Id, enabled: false);
        Assert.Contains(await scenario.Service.InspectAsync(scenario.State(), "device-a"), item => item.State == VolumeDiagnosticState.Disabled);
    }

    [Fact]
    public async Task ReapplyUsesCurrentEnabledTargetsWithoutChangingFileSnapshotOrOtherDevice()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var app = scenario.Raw.AddSession("a", .4f);
        scenario.OtherDevices.Add("device-b");
        var peer = scenario.Raw.AddSession("b", .7f, deviceId: "device-b");
        await scenario.StartAsync();
        var file = await File.ReadAllTextAsync(scenario.Store.FilePath);
        scenario.Raw.Sessions[app.Key] = app with { Volume = new(.9f, true) };
        scenario.Raw.Sessions[peer.Key] = peer with { Volume = new(.8f, true) };
        scenario.Raw.Master = new(.2f, true);
        scenario.Raw.Masters["device-b"] = new(.6f, true);
        await scenario.Service.ReapplyTargetsAsync(scenario.State(), "device-a");
        Assert.Equal(new AudioVolume(.5f, false), scenario.Raw.Master);
        Assert.Equal(app.Volume, scenario.Raw.Sessions[app.Key].Volume);
        Assert.Equal(new AudioVolume(.8f, true), scenario.Raw.Sessions[peer.Key].Volume);
        Assert.Equal(new AudioVolume(.6f, true), scenario.Raw.Masters["device-b"]);
        Assert.Equal(file, await File.ReadAllTextAsync(scenario.Store.FilePath));
    }

    [Fact]
    public async Task ExplicitReapplyRetriesFailureButDisabledAndOfflineTargetsAreNotWritten()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .4f);
        await scenario.StartAsync();
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.9f, false) };
        scenario.Raw.IgnoreWrites = true;
        await scenario.Service.ReapplyTargetsAsync(scenario.State(), "device-a");
        Assert.Contains(scenario.Reports, item => !item.Succeeded);
        scenario.Raw.IgnoreWrites = false;
        await scenario.Service.ReapplyTargetsAsync(scenario.State(), "device-a");
        Assert.Equal(.4f, scenario.Raw.Sessions[session.Key].Volume.Level);
        var profile = scenario.Service.ProfileForDevice("device-a")!;
        await scenario.Service.UpdateRuleAsync(profile.Id, "device-a", profile.Rules.Single(rule => rule.Target.Target.Application is not null).Id, enabled: false);
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false) };
        await scenario.Service.ReapplyTargetsAsync(scenario.State(), "device-a");
        Assert.Equal(.8f, scenario.Raw.Sessions[session.Key].Volume.Level);
        scenario.Listening = false;
        var writes = scenario.Raw.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.ReapplyTargetsAsync(scenario.State(), "device-a"));
        Assert.Equal(writes, scenario.Raw.Writes.Count);
    }

    [Fact]
    public async Task CommandsLogComparisonAndDoNotLoseRejectedReapplyFeedback()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .4f);
        await scenario.StartAsync();
        var log = new OperationLog();
        using var vm = new ConfigurationViewModel(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario),
            new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), log);
        await vm.InitializeAsync();
        vm.SetDevice("device-a", "耳机");
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.8f, false) };
        await vm.InspectCommand.ExecuteAsync();
        Assert.Contains(vm.Diagnostics, item => item.State == VolumeDiagnosticState.Differs);
        Assert.Contains(log.Entries, item => item.Text.Contains("目标 40%") && item.Text.Contains("实际 80%"));
        Assert.Contains("不代表听感", vm.Status);
        scenario.Raw.IgnoreWrites = true;
        await vm.ReapplyCommand.ExecuteAsync();
        Assert.Contains("系统未接受目标", vm.Status);
        Assert.True(vm.HasOperationError);
        scenario.Raw.IgnoreWrites = false;
        await vm.ReapplyCommand.ExecuteAsync();
        Assert.False(vm.HasOperationError);
    }

    [Fact]
    public async Task DeviceChangeDuringEditFlushCancelsQueuedReapplyInsteadOfApplyingAnotherDevice()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.OtherDevices.Add("device-b");
        await scenario.StartAsync();
        using var vm = new ConfigurationViewModel(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario),
            new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), new());
        await vm.InitializeAsync();
        vm.SetDevice("device-a", "耳机");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BeforeApply = () => release.Task;
        var operation = vm.ReapplyCommand.ExecuteAsync();
        vm.SetDevice("device-b", "扬声器");
        release.SetResult();
        await operation;
        Assert.Contains("已切换", vm.Status);
        Assert.Empty(scenario.Raw.Writes);
    }

    [Fact]
    public async Task ReapplyCannotOverwriteAnActiveVolumeEdit()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await scenario.StartAsync();
        var token = await scenario.Service.BeginVolumeEditAsync("device-a");
        scenario.Raw.Master = new(.73f, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.ReapplyTargetsAsync(scenario.State(), "device-a"));
        Assert.Equal(.73f, scenario.Raw.Master.Level);
        Assert.Empty(scenario.Raw.Writes);
        await scenario.Service.EndVolumeEditAsync(token);
    }
}
