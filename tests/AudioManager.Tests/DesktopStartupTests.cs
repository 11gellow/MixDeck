using AudioManager.Desktop.Services;

namespace AudioManager.Tests;

public sealed class DesktopStartupTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task AudioCompletesBeforePresentationAndRepeatedStartUsesSameTask(bool trayOnly, int windows)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = 0; var trays = 0; var shown = 0;
        var startup = new DesktopStartup(() => { audio++; return gate.Task; }, () => { trays++; return true; }, () => shown++);
        var task = startup.StartAsync(trayOnly, default);
        Assert.Same(task, startup.StartAsync(trayOnly, default));
        Assert.Equal(1, audio); Assert.Equal(0, trays); Assert.Equal(0, shown);
        gate.SetResult(); await task;
        Assert.Equal(1, trays); Assert.Equal(windows, shown);
    }

    [Fact]
    public async Task ExhaustedTrayRetriesExposeWindowAndCancellationStopsPresentation()
    {
        var tries = 0; var windows = 0;
        var startup = new DesktopStartup(() => Task.CompletedTask, () => { tries++; return false; }, () => windows++,
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);
        await startup.StartAsync(true, default);
        Assert.Equal(3, tries); Assert.Equal(1, windows);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        startup = new DesktopStartup(() => Task.CompletedTask, () => throw new Exception("must not create tray"), () => windows++);
        await startup.StartAsync(true, cancellation.Token);
        Assert.Equal(1, windows);
    }
}
