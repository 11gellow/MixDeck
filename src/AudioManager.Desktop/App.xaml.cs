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
    private bool closeDialogOpen;
    private DesktopInstance? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        var startup = new ScheduledLoginStartup(executable);
        var startupJournal = new StartupJournal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MixDeck", "diagnostics", "startup.jsonl"));
        // 显式一键注册/回退，不初始化音频，不显示主窗口，方便同一构建 A/B 对比。
        var registrations = e.Args.Where(arg => arg.StartsWith("--startup-register-", StringComparison.Ordinal)).ToArray();
        if (registrations.Length > 1)
        {
            startupJournal.Mark("registration-failed", "只能指定一种启动注册模式。");
            Shutdown(1);
            return;
        }
        var registration = registrations.SingleOrDefault();
        if (registration is not null)
        {
            try
            {
                switch (registration)
                {
                    case "--startup-register-run": startup.UseLegacy(); break;
                    case "--startup-register-task": startup.SetEnabled(true); break;
                    case "--startup-register-off": startup.SetEnabled(false); break;
                    default: throw new ArgumentException("未知启动注册模式。");
                }
                startupJournal.Mark("registration", startup.RegistrationKind);
                Shutdown(0);
            }
            catch (Exception exception) { startupJournal.Mark("registration-failed", exception.Message); Shutdown(1); }
            return;
        }
        instance = new DesktopInstance();
        if (!instance.IsPrimary)
        {
            if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) instance.RequestShow();
            Shutdown();
            return;
        }
        startupJournal.Mark("app-entry", e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) ? "tray" : "manual");
        try { startupJournal.Mark("startup-backend", startup.RegistrationKind); }
        catch (Exception exception) { startupJournal.Mark("startup-backend-unavailable", exception.Message); }
        services = new ServiceCollection()
            .AddLogging(logging => logging.AddDebug())
            .AddWindowsAudio()
            .AddSingleton<WindowsAudioController>()
            .AddSingleton(provider => new AudioWriteGuard(provider.GetRequiredService<WindowsAudioController>()))
            .AddSingleton<IAudioController>(provider => provider.GetRequiredService<AudioWriteGuard>())
            .AddSingleton<IConfigurationStore>(_ => new JsonConfigurationStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDeck", "settings.json")))
            .AddSingleton<ILoginStartup>(startup)
            .AddSingleton(startupJournal)
            .AddSingleton<AudioConfigurationService>()
            .AddSingleton<IConfigurationDialogs, ConfigurationDialogs>()
            .AddSingleton(_ => new ThemeService(Resources, Dispatcher, WindowsThemePreference.IsDark,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDeck", "appearance.json")))
            .AddSingleton<AppearanceViewModel>()
            .AddSingleton(_ => new WindowPreferencesStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDeck", "window.json")))
            .AddSingleton<WindowBehaviorViewModel>()
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
        instance.Listen(() => Dispatcher.BeginInvoke(() => { if (lifetime is { IsExiting: false }) lifetime.ShowWindow(); }));
        try
        {
            tray = new SystemTrayIcon(Dispatcher, lifetime.ShowWindow, lifetime.RequestExit,
                services.GetRequiredService<ILogger<SystemTrayIcon>>(),
                refreshCommand: services.GetRequiredService<MainViewModel>().RefreshCommand,
                reapplyCommand: services.GetRequiredService<ConfigurationViewModel>().ReapplyCommand);
            lifetime.EnableTray();
            startupJournal.Mark("tray-created");
        }
        catch (Exception exception)
        {
            services.GetRequiredService<OperationLog>().Error("系统托盘初始化失败；关闭主窗口将退出程序", exception);
        }
        MainWindow.Closing += OnMainWindowClosing;
        MainWindow.StateChanged += OnWindowStateChanged;
        SessionEnding += OnSessionEnding;
        if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) && lifetime.HasTray)
            MainWindow.Loaded += HideAfterLoginStartup;
        MainWindow.Show();
        startupJournal.Mark("window-shown");
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
        if (lifetime is not { IsExiting: false } || services is null) return;
        e.Cancel = true;
        if (closeDialogOpen) return;
        var preferences = services.GetRequiredService<WindowPreferencesStore>();
        var choice = preferences.CloseBehavior;
        if (choice == WindowCloseBehavior.Ask || choice == WindowCloseBehavior.Tray && !lifetime.HasTray)
        {
            closeDialogOpen = true;
            try
            {
                var dialog = new CloseChoiceDialog(lifetime.HasTray) { Owner = MainWindow };
                var themes = services.GetRequiredService<ThemeService>();
                dialog.SourceInitialized += (_, _) => WindowsThemePreference.ApplyTitleBar(
                    new System.Windows.Interop.WindowInteropHelper(dialog).Handle, themes.EffectiveTheme == AppThemeMode.Dark);
                if (dialog.ShowDialog() != true) return;
                choice = dialog.Choice;
                if (dialog.Remember)
                {
                    preferences.SetCloseBehavior(choice);
                    if (preferences.Error is { } error) services.GetRequiredService<OperationLog>().Warning(error, "窗口偏好");
                }
            }
            finally { closeDialogOpen = false; }
        }
        if (choice == WindowCloseBehavior.Tray) lifetime.TryHideWindow();
        else if (choice == WindowCloseBehavior.Exit) Dispatcher.BeginInvoke(lifetime.RequestExit);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MainWindow.WindowState == WindowState.Minimized) lifetime?.TryHideWindow();
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => lifetime?.PrepareSessionEnd();
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
        }
        if (updateTitleBar is not null && services is not null) services.GetRequiredService<ThemeService>().Changed -= updateTitleBar;
        try { tray?.Dispose(); }
        finally { services?.Dispose(); instance?.Dispose(); base.OnExit(e); }
    }
}
