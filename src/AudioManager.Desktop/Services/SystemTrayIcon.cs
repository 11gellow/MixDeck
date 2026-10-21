using System.Drawing;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace AudioManager.Desktop.Services;

/// <summary>仅处理通知区域入口；音频监听和窗口状态由现有应用生命周期管理。</summary>
internal sealed class SystemTrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon image;
    private readonly Forms.ToolStripMenuItem showItem;
    private readonly Forms.ToolStripMenuItem exitItem;
    private readonly Dispatcher dispatcher;
    private readonly Action show;
    private readonly Action exit;
    private readonly ILogger logger;
    private bool disposed;

    internal SystemTrayIcon(Dispatcher dispatcher, Action show, Action exit, ILogger logger, Forms.ContextMenuStrip? contextMenu = null)
    {
        this.dispatcher = dispatcher;
        this.show = show;
        this.exit = exit;
        this.logger = logger;
        image = (Icon)SystemIcons.Application.Clone();
        menu = contextMenu ?? new Forms.ContextMenuStrip();
        showItem = new Forms.ToolStripMenuItem("显示主窗口");
        exitItem = new Forms.ToolStripMenuItem("退出");
        showItem.Click += OnShow;
        exitItem.Click += OnExit;
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        icon = new Forms.NotifyIcon { Text = "Windows Audio Manager · 双击显示，右键退出", Icon = image, ContextMenuStrip = menu };
        icon.DoubleClick += OnShow;
        try { icon.Visible = true; }
        catch { Dispose(); throw; }
    }

    private void OnShow(object? sender, EventArgs args) => Dispatch(show);
    private void OnExit(object? sender, EventArgs args) => Dispatch(exit);

    private void Dispatch(Action action)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            try { action(); }
            catch (Exception exception) { logger.LogError(exception, "执行托盘操作失败"); }
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.DoubleClick -= OnShow;
        showItem.Click -= OnShow;
        exitItem.Click -= OnExit;
        try { icon.Visible = false; }
        finally { icon.Dispose(); menu.Dispose(); image.Dispose(); }
    }
}
