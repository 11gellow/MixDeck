using AudioManager.Desktop.Services;
using AudioManager.Desktop.ViewModels;
using AudioManager.Desktop.Views;
using AudioManager.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.ComponentModel;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Services;
using System.IO;

namespace AudioManager.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? services;
    private DesktopLifetime? lifetime;
    private SystemTrayIcon? tray;
    private Action? updateTitleBar;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        services = new ServiceCollection()
            .AddLogging(logging => logging.AddDebug())
            .AddWindowsAudio()
            .AddSingleton<WindowsAudioController>()
            .AddSingleton(provider => new AudioWriteGuard(provider.GetRequiredService<WindowsAudioController>()))
            .AddSingleton<IAudioController>(provider => provider.GetRequiredService<AudioWriteGuard>())
            .AddSingleton<IConfigurationStore>(_ => new JsonConfigurationStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDeck", "settings.json")))
            .AddSingleton<ILoginStartup>(_ => new WindowsLoginStartup(Path.ChangeExtension(typeof(App).Assembly.Location, ".exe")))
            .AddSingleton<AudioConfigurationService>()
            .AddSingleton<IConfigurationDialogs, ConfigurationDialogs>()
            .AddSingleton(_ => new ThemeService(Resources, Dispatcher, WindowsThemePreference.IsDark,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDeck", "appearance.json")))
            .AddSingleton<AppearanceViewModel>()
            .AddSingleton<ConfigurationViewModel>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<AppIconProvider>()
            .AddSingleton<OperationLog>()
            .AddSingleton(_ => new AudioEventJournal(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDeck", "diagnostics", "audio-events.jsonl")))
            .AddSingleton<MainViewModel>()
            .AddSingleton<MainWindow>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        MainWindow = services.GetRequiredService<MainWindow>();
        var themes = services.GetRequiredService<ThemeService>();
        updateTitleBar = () => WindowsThemePreference.ApplyTitleBar(new System.Windows.Interop.WindowInteropHelper(MainWindow).Handle,
            themes.EffectiveTheme == AppThemeMode.Dark);
        themes.Changed += updateTitleBar;
        MainWindow.SourceInitialized += OnWindowSourceInitialized;
        lifetime = new DesktopLifetime(ShowMainWindow, MainWindow.Hide, () => _ = ShutdownAfterFlushAsync());
        try
        {
            tray = new SystemTrayIcon(Dispatcher, lifetime.ShowWindow, lifetime.RequestExit,
                services.GetRequiredService<ILogger<SystemTrayIcon>>(),
                refreshCommand: services.GetRequiredService<MainViewModel>().RefreshCommand,
                reapplyCommand: services.GetRequiredService<ConfigurationViewModel>().ReapplyCommand);
            lifetime.EnableTray();
        }
        catch (Exception exception)
        {
            services.GetRequiredService<OperationLog>().Error("系统托盘初始化失败；关闭主窗口将退出程序", exception);
        }
        MainWindow.Closing += OnMainWindowClosing;
        MainWindow.StateChanged += OnWindowStateChanged;
        var mainView = (MainWindow)MainWindow;
        mainView.SetTrayAvailable(lifetime.HasTray);
        mainView.ExitRequested += OnExitRequested;
        SessionEnding += OnSessionEnding;
        if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) && lifetime.HasTray)
            MainWindow.Loaded += HideAfterLoginStartup;
        MainWindow.Show();
    }

    private void HideAfterLoginStartup(object sender, RoutedEventArgs e)
    {
        MainWindow.Loaded -= HideAfterLoginStartup;
        lifetime?.TryHideWindow();
    }

    private void ShowMainWindow()
    {
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (lifetime is { HasTray: true, IsExiting: false })
        {
            e.Cancel = true;
            lifetime.TryHideWindow();
        }
        else if (lifetime is { IsExiting: false }) Dispatcher.BeginInvoke(lifetime.RequestExit);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MainWindow.WindowState == WindowState.Minimized) lifetime?.TryHideWindow();
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => lifetime?.PrepareSessionEnd();
    private void OnExitRequested(object? sender, EventArgs e) => lifetime?.RequestExit();
    private void OnWindowSourceInitialized(object? sender, EventArgs e) => updateTitleBar?.Invoke();

    private async Task ShutdownAfterFlushAsync()
    {
        if (MainWindow is MainWindow main) await main.PrepareToLeaveAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SessionEnding -= OnSessionEnding;
        if (MainWindow is not null)
        {
            MainWindow.SourceInitialized -= OnWindowSourceInitialized;
            MainWindow.Closing -= OnMainWindowClosing;
            MainWindow.StateChanged -= OnWindowStateChanged;
            if (MainWindow is MainWindow mainView) mainView.ExitRequested -= OnExitRequested;
        }
        if (updateTitleBar is not null && services is not null) services.GetRequiredService<ThemeService>().Changed -= updateTitleBar;
        try { tray?.Dispose(); }
        finally { services?.Dispose(); base.OnExit(e); }
    }
}
