namespace AudioManager.Desktop.Services;

/// <summary>托盘可用时关闭/最小化只隐藏窗口；显式退出及系统注销不被隐藏逻辑拦截。</summary>
public sealed class DesktopLifetime(Action show, Action hide, Action shutdown)
{
    public bool HasTray { get; private set; }
    public bool IsExiting { get; private set; }

    public void EnableTray() => HasTray = true;

    public void ShowWindow()
    {
        if (!IsExiting) show();
    }

    public bool TryHideWindow()
    {
        if (!HasTray || IsExiting) return false;
        hide();
        return true;
    }

    public void RequestExit()
    {
        if (IsExiting) return;
        IsExiting = true;
        shutdown();
    }

    public void PrepareSessionEnd() => IsExiting = true;
}
