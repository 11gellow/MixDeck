using AudioManager.Desktop.Services;
using AudioManager.Desktop.ViewModels;
using AudioManager.Desktop.Views;
using AudioManager.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.ComponentModel;

namespace AudioManager.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? services;
    private DesktopLifetime? lifetime;
    private SystemTrayIcon? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        services = new ServiceCollection()
            .AddLogging(logging => logging.AddDebug())
            .AddWindowsAudio()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<AppIconProvider>()
            .AddSingleton<OperationLog>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<MainWindow>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        MainWindow = services.GetRequiredService<MainWindow>();
        lifetime = new DesktopLifetime(ShowMainWindow, MainWindow.Hide, () => Shutdown());
        try
        {
            tray = new SystemTrayIcon(Dispatcher, lifetime.ShowWindow, lifetime.RequestExit,
                services.GetRequiredService<ILogger<SystemTrayIcon>>());
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
        MainWindow.Show();
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

    protected override void OnExit(ExitEventArgs e)
    {
        SessionEnding -= OnSessionEnding;
        if (MainWindow is not null)
        {
            MainWindow.Closing -= OnMainWindowClosing;
            MainWindow.StateChanged -= OnWindowStateChanged;
            if (MainWindow is MainWindow mainView) mainView.ExitRequested -= OnExitRequested;
        }
        try { tray?.Dispose(); }
        finally { services?.Dispose(); base.OnExit(e); }
    }
}
