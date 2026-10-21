using System.Windows.Threading;
using AudioManager.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Forms = System.Windows.Forms;

namespace AudioManager.Tests;

public sealed class SystemTrayComponentTests
{
    [Fact]
    public async Task NativeTrayMenuDispatchesShowAndExitAndDisposesItsSurface()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var shows = 0;
                    var exits = 0;
                    var menu = new Forms.ContextMenuStrip();
                    var tray = new SystemTrayIcon(dispatcher, () => shows++, () => exits++, NullLogger.Instance, menu);
                    try
                    {
                        Assert.Equal("显示主窗口", menu.Items[0].Text);
                        Assert.Equal("退出", menu.Items[2].Text);
                        menu.Items[0].PerformClick();
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        Assert.Equal(1, shows);
                        Assert.Equal(0, exits);
                        menu.Items[2].PerformClick();
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        Assert.Equal(1, exits);
                    }
                    finally { tray.Dispose(); tray.Dispose(); }
                    Assert.True(menu.IsDisposed);
                    completion.TrySetResult();
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "AudioManager.TrayComponentTest" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
