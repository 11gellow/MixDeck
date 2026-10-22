using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Desktop.Services;
using AudioManager.Desktop.ViewModels;
using AudioManager.Windows;
using Microsoft.Win32;

namespace AudioManager.Tests;

public sealed class ConfigurationViewModelTests
{
    [Fact]
    public async Task FrontendOutputSelectionChangesSystemDefaultAndAppliesOnlyItsRememberedProfile()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.OtherDevices.Add("device-b");
        scenario.Raw.Master = new(.2f, false);
        scenario.Raw.Masters["device-b"] = new(.8f, true);
        await scenario.Service.HandleStateAsync(scenario.State());
        var targetProfile = scenario.Service.ProfileForDevice("device-b")!;
        var monitor = new Monitor(scenario);
        using var output = new DefaultOutput(id => scenario.DefaultDeviceId = id);
        using var configuration = new ConfigurationViewModel(scenario.Service, monitor, new FakeStartup(), new FakeDialogs(), new());
        using var main = new MainViewModel(scenario.Guard, monitor, new(), new(), TimeProvider.System, configuration, output);
        await main.InitializeAsync();
        scenario.Raw.Masters["device-b"] = new(.1f, false);
        main.SelectedDevice = main.Devices.Single(device => device.Id == "device-b");
        await main.OutputSwitchTask;
        Assert.Equal("device-b", scenario.DefaultDeviceId);
        Assert.True(main.SelectedDevice?.IsDefault);
        Assert.Equal(targetProfile.Id, scenario.Service.Current.ActiveProfileId);
        Assert.Equal(targetProfile.Name, configuration.CurrentProfileName);
        Assert.Equal(80, main.MasterVolume);
        Assert.True(main.IsMasterMuted);
        Assert.Equal(new AudioManager.Core.Models.AudioVolume(.2f, false), scenario.Raw.Master);
        Assert.True(main.CanControlMaster);
    }

    [Fact]
    public async Task CommandsSaveSelectEditRestoreImportExportAndResetThroughBusinessService()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .37f);
        var startup = new FakeStartup();
        var dialogs = new FakeDialogs();
        using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), startup, dialogs, new());
        await vm.InitializeAsync();
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "我的方案");
        Assert.Equal("我的方案", vm.CurrentProfileName);
        Assert.Single(vm.Profiles);
        vm.Rules.Single(row => row.Match.EndsWith("Player.exe")).VolumeText = "22";
        await vm.CommitInputsAsync();
        Assert.Equal(.22f, scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
        vm.StartAtLogin = true;
        vm.ShowUnavailableDevices = true;
        await vm.WhenIdleAsync();
        Assert.True(startup.IsEnabled);
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.9f, true) };
        await vm.RestoreCommand.ExecuteAsync();
        Assert.Equal(session.Volume, scenario.Raw.Sessions[session.Key].Volume);
        dialogs.Export = Path.Combine(scenario.Directory, "export.json");
        await vm.ExportCommand.ExecuteAsync();
        dialogs.Import = dialogs.Export;
        await vm.ImportCommand.ExecuteAsync();
        Assert.DoesNotContain("automaticRestoreEnabled", await File.ReadAllTextAsync(scenario.Store.FilePath));
        await vm.ResetCommand.ExecuteAsync();
        Assert.False(startup.IsEnabled);
        Assert.Empty(vm.Profiles);
    }

    [Fact]
    public async Task InvalidRuleAndDeniedStartupDoNotOverwriteSavedSettingsOrSnapshot()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("a", .4f);
        var startup = new FakeStartup();
        using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), startup, new FakeDialogs(), new());
        await vm.InitializeAsync();
        await scenario.Service.HandleStateAsync(scenario.State());
        var before = await File.ReadAllTextAsync(scenario.Store.FilePath);
        vm.Rules[0].VolumeText = "not-a-volume";
        await vm.CommitInputsAsync();
        Assert.Contains("0～100", vm.Status);
        Assert.Equal(before, await File.ReadAllTextAsync(scenario.Store.FilePath));
        startup.Failure = new UnauthorizedAccessException("注册表拒绝访问");
        vm.StartAtLogin = true;
        await vm.WhenIdleAsync();
        Assert.Contains("拒绝访问", vm.Status);
        Assert.False(scenario.Service.Current.Settings.StartAtLogin);
    }

    [Fact]
    public async Task FailedPersistenceRollsBackStartupAndSurfacesError()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var startup = new FakeStartup();
        using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), startup, new FakeDialogs(), new());
        await vm.InitializeAsync();
        // 另一个进程创建文件，触发旧实例覆盖保护。
        await File.WriteAllTextAsync(scenario.Store.FilePath, "{}");
        vm.StartAtLogin = true;
        await vm.WhenIdleAsync();
        Assert.False(startup.IsEnabled);
        Assert.Contains("另一个进程", vm.Status);
        Assert.False(scenario.Service.Current.Settings.StartAtLogin);
    }

    [Fact]
    public async Task MainAndSessionControlsDisableAfterRejectedWriteAndShowActualValues()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .4f);
        using var vm = new MainViewModel(scenario.Guard, new Monitor(scenario), new(), new(), TimeProvider.System);
        await vm.InitializeAsync();
        scenario.Raw.IgnoreWrites = true;
        vm.MasterVolume = 10;
        Assert.Equal(50, vm.MasterVolume);
        Assert.False(vm.CanControlMaster);
        Assert.Contains("无法修改", vm.MasterControlStatus);
        vm.Sessions[0].Volume = 20;
        Assert.Equal(40, vm.Sessions[0].Volume);
        Assert.False(vm.Sessions[0].CanControl);
        Assert.Contains("无法修改", vm.Sessions[0].ControlStatus);
        var writes = scenario.Raw.Writes.Count;
        vm.Sessions[0].Volume = 30;
        Assert.Equal(writes, scenario.Raw.Writes.Count);
        scenario.Raw.IgnoreWrites = false;
        await vm.RefreshCommand.ExecuteAsync();
        Assert.True(vm.CanControlMaster);
        Assert.True(vm.Sessions[0].CanControl);
        Assert.Equal(session.Volume, scenario.Raw.Sessions[session.Key].Volume);
    }

    [Fact]
    public async Task DeviceProfileDoesNotDisableManualControlsAndFrontendVolumeIsSavedOnEvent()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .4f);
        var monitor = new Monitor(scenario);
        using var configuration = new ConfigurationViewModel(scenario.Service, monitor, new FakeStartup(), new FakeDialogs(), new());
        using var vm = new MainViewModel(scenario.Guard, monitor, new(), new(), TimeProvider.System, configuration);
        await vm.InitializeAsync();
        Assert.True(vm.CanControlMaster);
        Assert.True(vm.Sessions[0].CanControl);
        vm.MasterVolume = 28;
        vm.Sessions[0].Volume = 63;
        await scenario.PushAsync(AudioStateChangeKind.MasterVolumeChanged, AudioChangeOrigin.Self);
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.Self, session.Key);
        var rules = scenario.Service.ProfileForDevice("device-a")!.Rules;
        Assert.Equal(.28f, rules.Single(rule => rule.Target.Target.Application is null).Target.Volume.Level);
        Assert.Equal(.63f, rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
        Assert.True(vm.CanControlMaster);
        Assert.True(vm.Sessions[0].CanControl);
        await scenario.Service.RestoreSnapshotAsync(scenario.State(), scenario.Service.Current.ActiveProfileId!.Value);
        Assert.Equal(50, vm.MasterVolume);
        Assert.Equal(40, vm.Sessions[0].Volume);
    }

    [Fact]
    public async Task ViewingDeviceFiltersProfilesAndSavingSettingsDoesNotRestoreOldVolume()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.OtherDevices.Add("device-b");
        await scenario.Service.HandleStateAsync(scenario.State());
        using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), new FakeStartup(), new FakeDialogs(), new());
        await vm.InitializeAsync();
        vm.SetDevice("device-b", "扬声器");
        Assert.Equal(scenario.Service.ProfileForDevice("device-b")!.Name, vm.CurrentProfileName);
        Assert.Single(vm.Profiles);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "扬声器工作", "device-b");
        Assert.Equal("扬声器工作", vm.CurrentProfileName);
        Assert.All(vm.SelectedProfile!.Rules, rule => Assert.Equal("device-b", rule.Target.Target.DeviceId));
        Assert.Equal(scenario.Service.ProfileForDevice("device-a")!.Id, scenario.Service.Current.ActiveProfileId);
        scenario.Raw.Masters["device-b"] = new(.17f, false);
        await vm.WhenIdleAsync();
        Assert.Equal(.17f, scenario.Raw.Masters["device-b"].Level);
    }

    [Fact]
    public async Task SingleProfileInlineCommitMergesExternalVolumeAndDoesNotLeaveStaleDraft()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .4f);
        await scenario.Service.HandleStateAsync(scenario.State());
        using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), new FakeStartup(), new FakeDialogs(), new());
        await vm.InitializeAsync();
        vm.StartAtLogin = true;
        vm.Rules.Single(row => row.Match.EndsWith("Player.exe")).VolumeText = "21";
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.64f, false) };
        await scenario.PushAsync(AudioStateChangeKind.SessionVolumeChanged, AudioChangeOrigin.External, session.Key);
        Assert.True(vm.StartAtLogin);
        Assert.Equal("21", vm.Rules.Single(row => row.Match.EndsWith("Player.exe")).VolumeText);
        var row = vm.Rules.Single(row => row.Match.EndsWith("Player.exe"));
        row.Enabled = false;
        await vm.WhenIdleAsync();
        Assert.Equal(.64f, scenario.Service.ProfileForDevice("device-a")!.Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
        await vm.CommitInputsAsync();
        Assert.Equal(.21f, scenario.Service.ProfileForDevice("device-a")!.Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
        Assert.False(row.Enabled);
        Assert.Single(vm.Profiles);
        Assert.Equal("21", row.VolumeText);
    }

    [Fact]
    public async Task LegacyMultiDeviceProfileEditsOnlyViewedDeviceAndKeepsOtherTargets()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.OtherDevices.Add("device-b");
        scenario.Raw.Masters["device-b"] = new(.82f, false);
        await scenario.Service.HandleStateAsync(scenario.State());
        var a = scenario.Service.ProfileForDevice("device-a")!;
        var b = scenario.Service.ProfileForDevice("device-b")!;
        var imported = scenario.Service.Current with
        {
            Profiles = [a with { Rules = a.Rules.Concat(b.Rules).ToArray(), Snapshot = a.Snapshot with { Entries = a.Snapshot.Entries.Concat(b.Snapshot.Entries).ToArray() } }],
            DeviceProfiles = [new("device-a", a.Id), new("device-b", a.Id)]
        };
        var path = Path.Combine(scenario.Directory, "multi.json");
        await File.WriteAllTextAsync(path, AudioManager.Core.Configuration.ConfigurationJson.Serialize(imported));
        await scenario.Service.ImportAsync(path);
        using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), new FakeStartup(), new FakeDialogs(), new());
        await vm.InitializeAsync();
        vm.SetDevice("device-a", "耳机");
        Assert.Single(vm.Rules);
        vm.Rules[0].VolumeText = "33";
        scenario.Raw.Masters["device-b"] = new(.41f, false); // 另一个设备的调节通知尚未消费。
        await vm.CommitInputsAsync();
        Assert.Equal(.33f, scenario.Raw.Master.Level);
        Assert.Equal(.41f, scenario.Raw.Masters["device-b"].Level);
        Assert.Equal(.82f, scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Target.Target.DeviceId == "device-b").Target.Volume.Level);
    }

    [Fact]
    public void WindowsLoginRegistrationQuotesExecutableAndUsesTrayArgumentAndReleasesTestKey()
    {
        var key = @"Software\MixDeck.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var startup = new WindowsLoginStartup(@"C:\Program Files\MixDeck\MixDeck.exe", "Test", key);
            Assert.Equal(@"""C:\Program Files\MixDeck\MixDeck.exe"" --tray", startup.Command);
            Assert.False(startup.IsEnabled);
            startup.SetEnabled(true);
            Assert.True(startup.IsEnabled);
            using (var opened = Registry.CurrentUser.OpenSubKey(key)) Assert.Equal(startup.Command, opened?.GetValue("Test"));
            startup.SetEnabled(false);
            startup.SetEnabled(false);
            Assert.False(startup.IsEnabled);
            Assert.Throws<ArgumentException>(() => WindowsLoginStartup.BuildCommand("relative.exe"));
            Assert.Throws<ArgumentException>(() => WindowsLoginStartup.BuildCommand("C:\\bad\".exe"));
            Assert.Throws<ArgumentException>(() => WindowsLoginStartup.BuildCommand("C:\\" + new string('a', 260) + ".exe"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }

    [Fact]
    public async Task AdvancedOptionsLoadsInlineWithMainWindowTabsOnStaDispatcher()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("a", .4f);
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "布局");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            System.Windows.Application? application = null;
            try
            {
                // 加载与产品相同的资源及真实 XAML，验证 WPF 可创建、布局和绑定。
                application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var resources = new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/AudioManager.Desktop;component/Resources/Styles.xaml", UriKind.Absolute) };
                application.Resources.MergedDictionaries.Add(resources);
                using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), new FakeStartup(), new FakeDialogs(), new());
                Pump(vm.InitializeAsync());
                using var main = new MainViewModel(scenario.Guard, new Monitor(scenario), new(), new(), TimeProvider.System, vm);
                var window = new AudioManager.Desktop.Views.MainWindow(main);
                window.Measure(new(820, 700));
                window.Arrange(new(0, 0, 820, 700));
                window.UpdateLayout();
                Assert.Equal(main, window.DataContext);
                var pages = (System.Windows.Controls.TabControl)window.FindName("Pages");
                Assert.Equal(2, pages.Items.Count);
                Assert.Equal("音量管理", ((System.Windows.Controls.TabItem)pages.Items[0]).Header);
                Assert.Equal("高级选项", ((System.Windows.Controls.TabItem)pages.Items[1]).Header);
                Assert.IsType<AudioManager.Desktop.Views.AdvancedOptionsView>(((System.Windows.Controls.TabItem)pages.Items[1]).Content);
                pages.SelectedIndex = 1;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                window.UpdateLayout();
                var advanced = (AudioManager.Desktop.Views.AdvancedOptionsView)((System.Windows.Controls.TabItem)pages.Items[1]).Content;
                Assert.Equal(vm, advanced.DataContext);
                advanced.Measure(new(820, 700));
                advanced.Arrange(new(0, 0, 820, 700));
                advanced.UpdateLayout();
                var grid = Descendants(advanced).OfType<System.Windows.Controls.DataGrid>().Single();
                Assert.Equal(6, grid.Columns.Count);
                Assert.Equal(2, grid.Items.Count);
                Assert.DoesNotContain(Descendants(advanced).OfType<System.Windows.Controls.Button>(), button => button.Content?.ToString()?.Contains("保存") == true);
                using var inputSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("MixDeck test input") { Width = 1, Height = 1 });
                var input = Descendants(advanced).OfType<System.Windows.Controls.TextBox>().First();
                var editedRule = (RestoreRuleViewModel)input.DataContext;
                input.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "42");
                input.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
                Assert.Equal("42", editedRule.VolumeText);
                input.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, inputSource, Environment.TickCount, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Pump(vm.WhenIdleAsync());
                Assert.True(editedRule.VolumeText == "42", vm.Status + "; actual=" + editedRule.VolumeText);
                Assert.Equal(.42f, scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Id == editedRule.Id).Target.Volume.Level);
                input.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "99");
                input.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
                input.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, inputSource, Environment.TickCount, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Assert.Equal("42", editedRule.VolumeText);
                input.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "37");
                input.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
                input.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, Environment.TickCount, input, null)
                    { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
                Pump(vm.WhenIdleAsync());
                Assert.Equal(.37f, scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Id == editedRule.Id).Target.Volume.Level);
                Assert.Single(vm.Profiles);
                Assert.Equal(2, vm.Rules.Count);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
            finally { application?.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(Task task)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    internal sealed class FakeStartup : ILoginStartup
    {
        public bool IsEnabled { get; private set; }
        public Exception? Failure { get; set; }
        public void SetEnabled(bool enabled) { if (Failure is { } failure) throw failure; IsEnabled = enabled; }
    }
    private sealed class DefaultOutput(Action<string> change) : IDefaultAudioOutputController
    {
        public Task SetDefaultOutputDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        { change(deviceId); return Task.CompletedTask; }
        public void Dispose() { }
    }
    internal sealed class FakeDialogs : IConfigurationDialogs
    {
        public string? Import { get; set; }
        public string? Export { get; set; }
        public bool Confirmed { get; set; } = true;
        public string? ChooseImport() => Import;
        public string? ChooseExport() => Export;
        public bool Confirm(string message) => Confirmed;
    }
    internal sealed class Monitor(ConfigurationScenario scenario) : IAudioStateMonitor
    {
        public Task<AudioRuntimeState> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(scenario.State());
        public Task<AudioRuntimeState> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(scenario.State());
        public Task<AudioRuntimeState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(scenario.State());
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<AudioStateChanged> ReadChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
        public void Dispose() { }
    }
}
