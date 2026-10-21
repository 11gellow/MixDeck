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
    public async Task CommandsSaveSelectEditRestoreImportExportAndResetThroughBusinessService()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        var session = scenario.Raw.AddSession("a", .37f);
        var startup = new FakeStartup();
        var dialogs = new FakeDialogs();
        using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), startup, dialogs, new());
        await vm.InitializeAsync();
        vm.ProfileName = "我的方案";
        await vm.SaveSnapshotCommand.ExecuteAsync();
        Assert.Equal("我的方案", vm.CurrentProfileName);
        Assert.Single(vm.Profiles);
        vm.Rules.Single(row => row.Match.EndsWith("Player.exe")).VolumeText = "22";
        await vm.SaveRulesCommand.ExecuteAsync();
        Assert.Equal(.22f, scenario.Service.Current.Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
        vm.AutomaticRestore = true;
        vm.StartAtLogin = true;
        vm.ShowUnavailableDevices = true;
        await vm.SaveSettingsCommand.ExecuteAsync();
        Assert.True(startup.IsEnabled);
        Assert.True(scenario.Service.Current.Settings.AutomaticRestoreEnabled);
        await vm.RestoreCommand.ExecuteAsync();
        Assert.Contains("停用自动恢复", vm.Status);
        vm.AutomaticRestore = false;
        await vm.SaveSettingsCommand.ExecuteAsync();
        scenario.Raw.Sessions[session.Key] = session with { Volume = new(.9f, true) };
        await vm.RestoreCommand.ExecuteAsync();
        Assert.Equal(session.Volume, scenario.Raw.Sessions[session.Key].Volume);
        dialogs.Export = Path.Combine(scenario.Directory, "export.json");
        await vm.ExportCommand.ExecuteAsync();
        dialogs.Import = dialogs.Export;
        await vm.ImportCommand.ExecuteAsync();
        Assert.False(vm.AutomaticRestore);
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
        await vm.SaveSnapshotCommand.ExecuteAsync();
        var before = await File.ReadAllTextAsync(scenario.Store.FilePath);
        vm.Rules[0].VolumeText = "not-a-volume";
        await vm.SaveRulesCommand.ExecuteAsync();
        Assert.Contains("0～100", vm.Status);
        Assert.Equal(before, await File.ReadAllTextAsync(scenario.Store.FilePath));
        startup.Failure = new UnauthorizedAccessException("注册表拒绝访问");
        vm.StartAtLogin = true;
        await vm.SaveSettingsCommand.ExecuteAsync();
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
        await vm.SaveSettingsCommand.ExecuteAsync();
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
    public async Task ManagedTargetsDisableManualControlsAndReleaseWhenAutomaticRestoreStops()
    {
        using var scenario = await ConfigurationScenario.CreateAsync();
        scenario.Raw.AddSession("a", .4f);
        var monitor = new Monitor(scenario);
        using var configuration = new ConfigurationViewModel(scenario.Service, monitor, new FakeStartup(), new FakeDialogs(), new());
        using var vm = new MainViewModel(scenario.Guard, monitor, new(), new(), TimeProvider.System, configuration);
        await vm.InitializeAsync();
        await scenario.Service.SaveSnapshotAsync(scenario.State(), "规则");
        await scenario.EnableAsync();
        Assert.False(vm.CanControlMaster);
        Assert.False(vm.Sessions[0].CanControl);
        Assert.Contains("方案", vm.Sessions[0].ControlStatus);
        await scenario.Service.UpdateSettingsAsync(scenario.Service.Current.Settings with { AutomaticRestoreEnabled = false });
        Assert.True(vm.CanControlMaster);
        Assert.True(vm.Sessions[0].CanControl);
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
    public async Task ConfigurationWindowLoadsBindingsAndRuleColumnsOnStaDispatcher()
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
                var resources = new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/AudioManager.Desktop;component/Resources/Styles.xaml", UriKind.Absolute) };
                application.Resources.MergedDictionaries.Add(resources);
                using var vm = new ConfigurationViewModel(scenario.Service, new Monitor(scenario), new FakeStartup(), new FakeDialogs(), new());
                vm.InitializeAsync().GetAwaiter().GetResult();
                var window = new AudioManager.Desktop.Views.ConfigurationWindow(vm);
                window.Measure(new(820, 700));
                window.Arrange(new(0, 0, 820, 700));
                window.UpdateLayout();
                Assert.Equal(vm, window.DataContext);
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

    internal sealed class FakeStartup : ILoginStartup
    {
        public bool IsEnabled { get; private set; }
        public Exception? Failure { get; set; }
        public void SetEnabled(bool enabled) { if (Failure is { } failure) throw failure; IsEnabled = enabled; }
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
