using AudioManager.Windows.Interop;

namespace AudioManager.Tests;

public sealed class AudioThreadTests
{
    [Fact]
    public async Task CallsAndCleanupStayOnSameMtaThread()
    {
        var initializedOn = 0;
        var disposedOn = 0;
        var worker = new AudioThread(() =>
        {
            initializedOn = Environment.CurrentManagedThreadId;
            return new Cleanup(() => disposedOn = Environment.CurrentManagedThreadId);
        });
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => worker.InvokeAsync(
                () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()), default)));
            Assert.All(results, result =>
            {
                Assert.Equal(initializedOn, result.Item1);
                Assert.Equal(ApartmentState.MTA, result.Item2);
            });
        }
        finally { worker.Dispose(); }
        Assert.Equal(initializedOn, disposedOn);
        Assert.NotEqual(0, disposedOn);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotRunAndDisposeDrainsAcceptedWork()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var worker = new AudioThread();
        var cancelledRan = false;
        var first = worker.InvokeAsync(() => { entered.Set(); return release.Wait(TimeSpan.FromSeconds(10)); }, default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var cancelled = worker.InvokeAsync(() => { cancelledRan = true; return 1; }, cancellation.Token);
            var last = worker.InvokeAsync(() => 42, default);
            cancellation.Cancel();
            release.Set();
            worker.Dispose();
            Assert.True(await first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(cancelledRan);
            Assert.Equal(42, await last);
            Assert.Throws<ObjectDisposedException>(() => { _ = worker.InvokeAsync(() => 1, default); });
        }
        finally { release.Set(); worker.Dispose(); }
    }

    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
}
