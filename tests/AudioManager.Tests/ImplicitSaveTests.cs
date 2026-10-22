using System.IO;
using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Models;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Tests;

public sealed class ImplicitSaveTests
{
    [Fact]
    public async Task DragWritesSystemImmediatelyButSavesOnlyFinalActualAndLeavesOtherTargetsLive()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("one", .4f);
        var peer = scenario.Raw.AddSession("peer", .4f);
        await scenario.StartAsync();
        using var configuration = CreateViewModel(scenario);
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), new(), TimeProvider.System, configuration);
        await main.InitializeAsync();
        var row = main.Sessions.Single(item => item.Key == session.Key);
        main.BeginVolumeEdit(row);
        row.Volume = 21;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        Assert.Equal(.21f, scenario.Raw.Sessions[session.Key].Volume.Level);
        Assert.Equal(.4f, ApplicationRule(scenario).Target.Volume.Level);
        row.Volume = 68;
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        Assert.Equal(.4f, ApplicationRule(scenario).Target.Volume.Level);
        scenario.Raw.Master = new(.27f, true);
        await scenario.PushAsync(AudioStateChangeKind.MasterVolumeChanged, AudioChangeOrigin.External);
        Assert.Equal(new SavedVolume(.27f, true), scenario.Service.ProfileForDevice("device-a")!.Rules.Single(rule => rule.Target.Target.Application is null).Target.Volume);
        await main.FinishVolumeEditsAsync();
        Assert.Equal(.68f, ApplicationRule(scenario).Target.Volume.Level);
        Assert.Equal(.68f, scenario.Raw.Sessions[peer.Key].Volume.Level);
        var saved = await File.ReadAllTextAsync(scenario.Store.FilePath);
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        Assert.Equal(saved, await File.ReadAllTextAsync(scenario.Store.FilePath));
        Assert.Equal(.4f, scenario.Service.Current.Profiles[0].Snapshot.Entries.Single(entry => entry.Target.Application is not null).Volume.Level);
    }

    [Fact]
    public async Task ReleaseWithoutCallbackStillReadsActualAndForcedFlushEndsMasterDrag()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        using var configuration = CreateViewModel(scenario);
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), new(), TimeProvider.System, configuration);
        await main.InitializeAsync();
        main.BeginVolumeEdit();
        main.MasterVolume = 33;
        Assert.Equal(.33f, scenario.Raw.Master.Level);
        Assert.Equal(.5f, scenario.Service.Current.Profiles[0].Rules[0].Target.Volume.Level);
        await main.FlushEditsAsync();
        Assert.Equal(.33f, scenario.Service.Current.Profiles[0].Rules[0].Target.Volume.Level);
        await main.FlushEditsAsync(); // 捕获丢失、松手和关闭同时收尾可重入。
    }

    [Fact]
    public async Task DisappearingSessionReleasesEditAndDoesNotReplaceSavedTarget()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("one", .4f);
        await scenario.StartAsync();
        var token = await scenario.Service.BeginVolumeEditAsync("device-a", session.Key);
        scenario.Raw.Sessions.Remove(session.Key);
        await scenario.PushAsync(AudioStateChangeKind.SessionRemoved, AudioChangeOrigin.System, session.Key);
        await scenario.Service.EndVolumeEditAsync(token);
        Assert.Equal(.4f, ApplicationRule(scenario).Target.Volume.Level);
        Assert.Contains(scenario.Reports, item => item.Message.Contains("已失效"));
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.63f, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        Assert.Equal(.63f, ApplicationRule(scenario).Target.Volume.Level);
    }

    [Fact]
    public async Task DisconnectedDeviceAndRejectedWriteKeepTargetsAndReleaseScope()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await scenario.StartAsync();
        var token = await scenario.Service.BeginVolumeEditAsync("device-a");
        scenario.Listening = false;
        await scenario.PushAsync(AudioStateChangeKind.DeviceListeningChanged, AudioChangeOrigin.System);
        await scenario.Service.EndVolumeEditAsync(token);
        Assert.Equal(.5f, scenario.Service.Current.Profiles[0].Rules[0].Target.Volume.Level);
        scenario.Listening = true;
        await scenario.PushAsync(AudioStateChangeKind.DeviceListeningChanged, AudioChangeOrigin.Recovery);
        token = await scenario.Service.BeginVolumeEditAsync("device-a");
        scenario.Raw.IgnoreWrites = true;
        await scenario.Guard.SetMasterVolumeAsync("device-a", .1f);
        await scenario.Service.EndVolumeEditAsync(token);
        Assert.Equal(.5f, scenario.Service.Current.Profiles[0].Rules[0].Target.Volume.Level);
    }

    [Fact]
    public async Task EditCannotSaveIntoAnotherProfileAndFailedSaveRetainsActualVolume()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await scenario.StartAsync();
        var old = scenario.Service.Current.Profiles[0];
        var token = await scenario.Service.BeginVolumeEditAsync("device-a");
        scenario.Raw.Master = new(.72f, false);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "导入前已有方案");
        await scenario.Service.EndVolumeEditAsync(token);
        Assert.Equal(.5f, scenario.Service.Current.Profiles.Single(item => item.Id == old.Id).Rules[0].Target.Volume.Level);
        token = await scenario.Service.BeginVolumeEditAsync("device-a");
        scenario.Raw.Master = new(.39f, false);
        await File.WriteAllTextAsync(scenario.Store.FilePath, "external");
        await scenario.Service.EndVolumeEditAsync(token);
        Assert.Equal(.39f, scenario.Raw.Master.Level);
        Assert.Equal(.72f, scenario.Service.ProfileForDevice("device-a")!.Rules[0].Target.Volume.Level);
        Assert.Equal("external", await File.ReadAllTextAsync(scenario.Store.FilePath));
        Assert.Contains(scenario.Reports, item => item.Message.Contains("自动保存失败"));
    }

    [Fact]
    public async Task RulePatchPreservesLatestMuteVolumeOtherDeviceAndSnapshot()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("one", .4f);
        scenario.OtherDevices.Add("device-b");
        await scenario.StartAsync();
        var profile = scenario.Service.ProfileForDevice("device-a")!;
        var rule = ApplicationRule(scenario);
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.64f, true) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        await scenario.Service.UpdateRuleAsync(profile.Id, "device-a", rule.Id, enabled: false);
        Assert.Equal(new SavedVolume(.64f, true), ApplicationRule(scenario).Target.Volume);
        await scenario.Service.UpdateRuleAsync(profile.Id, "device-a", rule.Id, level: .21f);
        Assert.Equal(new SavedVolume(.21f, true), ApplicationRule(scenario).Target.Volume);
        Assert.Equal(profile.Snapshot, scenario.Service.ProfileForDevice("device-a")!.Snapshot);
        Assert.Equal(.5f, scenario.Service.ProfileForDevice("device-b")!.Rules[0].Target.Volume.Level);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Service.UpdateRuleAsync(profile.Id, "device-b", rule.Id, level: .1f));
    }

    [Fact]
    public async Task TogglesSaveImmediatelyAndNumericCancelInvalidAndDeviceChangeLeaveNoDraft()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.OtherDevices.Add("device-b");
        await scenario.StartAsync();
        var startup = new ConfigurationViewModelTests.FakeStartup();
        using var vm = CreateViewModel(scenario, startup);
        await vm.InitializeAsync();
        vm.SetDevice("device-a", "耳机");
        vm.StartAtLogin = true;
        vm.ShowUnavailableDevices = true;
        await vm.WhenIdleAsync();
        Assert.True(startup.IsEnabled);
        Assert.True(scenario.Service.Current.Settings.StartAtLogin);
        Assert.True(scenario.Service.Current.Settings.ShowUnavailableDevices);
        var row = vm.Rules[0];
        row.VolumeText = "12";
        row.CancelVolume();
        Assert.Equal("50", row.VolumeText);
        row.VolumeText = "NaN";
        await row.CommitVolumeAsync();
        Assert.Equal("50", row.VolumeText);
        Assert.Contains("0～100", vm.Status);
        row.IsMuted = true;
        await vm.WhenIdleAsync();
        Assert.True(scenario.Service.ProfileForDevice("device-a")!.Rules[0].Target.Volume.IsMuted);
        row.VolumeText = "23";
        vm.SetDevice("device-b", "扬声器");
        await vm.WhenIdleAsync();
        Assert.Equal(.23f, scenario.Service.ProfileForDevice("device-a")!.Rules[0].Target.Volume.Level);
        Assert.Equal("50", vm.Rules[0].VolumeText);
    }

    [Fact]
    public async Task SelectingExistingSchemeAppliesAndPersistsWithoutActivateButton()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "旧方案");
        var first = scenario.Service.Current.Profiles[0];
        scenario.Raw.Master = new(.7f, true);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "已有第二方案");
        await scenario.StartAsync();
        using var vm = CreateViewModel(scenario);
        await vm.InitializeAsync();
        vm.SetDevice("device-a", "耳机");
        vm.SelectedProfile = vm.Profiles.Single(item => item.Id == first.Id);
        await vm.WhenIdleAsync();
        Assert.Equal(first.Id, scenario.Service.ProfileForDevice("device-a")!.Id);
        Assert.Equal(new AudioVolume(.5f, false), scenario.Raw.Master);
        Assert.Equal(first.Id, ConfigurationJson.Deserialize(await File.ReadAllTextAsync(scenario.Store.FilePath)).ActiveProfileId);
    }

    [Fact]
    public async Task MainPreferenceAutosavesAndUnavailableConfigurationDoesNotBlockVolumeControl()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        using var configuration = CreateViewModel(scenario);
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), new(), TimeProvider.System, configuration);
        await main.InitializeAsync();
        main.ShowUnavailableDevices = true;
        await configuration.WhenIdleAsync();
        Assert.True(scenario.Service.Current.Settings.ShowUnavailableDevices);
        await scenario.Service.ResetAsync(); // 无设备方案也必须允许普通音量操作。
        main.BeginVolumeEdit();
        main.MasterVolume = 29;
        await main.FinishVolumeEditsAsync();
        Assert.Equal(.29f, scenario.Raw.Master.Level);
        Assert.True(main.CanControlMaster);
    }

    [Fact]
    public async Task LatestWriterDrainWaitsForInFlightAndFinalQueuedWrite()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<int>();
        var writer = new LatestValueWriter<int>(async value => { await release.Task; written.Add(value); });
        var writing = writer.SubmitAsync(1);
        await writer.SubmitAsync(2);
        await writer.SubmitAsync(3);
        var idle = writer.WhenIdleAsync();
        Assert.False(idle.IsCompleted);
        release.SetResult();
        await idle;
        await writing;
        Assert.Equal([1, 3], written);
    }

    private static RestoreRule ApplicationRule(ConfigurationScenario scenario)
        => scenario.Service.ProfileForDevice("device-a")!.Rules.Single(rule => rule.Target.Target.Application is not null);
    private static ConfigurationViewModel CreateViewModel(ConfigurationScenario scenario, ConfigurationViewModelTests.FakeStartup? startup = null)
        => new(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario), startup ?? new(), new ConfigurationViewModelTests.FakeDialogs(), new());
}
