namespace AudioManager.Desktop.Services;

/// <summary>同一 UI Dispatcher 内启动音频，之后才创建托盘/窗口；不拥有第二套音频服务。</summary>
internal sealed class DesktopStartup(Func<Task> initializeAudio, Func<bool> createTray, Action showWindow,
    TimeProvider? time = null, TimeSpan[]? retryDelays = null)
{
    private Task? started;
    internal Task StartAsync(bool trayOnly, CancellationToken cancellationToken) =>
        started ??= StartCoreAsync(trayOnly, cancellationToken);

    private async Task StartCoreAsync(bool trayOnly, CancellationToken cancellationToken)
    {
        await initializeAudio();
        if (cancellationToken.IsCancellationRequested) return;
        if (createTray())
        {
            if (!trayOnly) showWindow();
            return;
        }
        if (!trayOnly) showWindow();
        foreach (var delay in retryDelays ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
        {
            await Task.Delay(delay, time ?? TimeProvider.System, cancellationToken);
            if (createTray()) return;
        }
        if (trayOnly && !cancellationToken.IsCancellationRequested) showWindow();
    }
}
