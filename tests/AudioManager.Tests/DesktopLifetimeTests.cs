using AudioManager.Desktop.Services;

namespace AudioManager.Tests;

public sealed class DesktopLifetimeTests
{
    [Fact]
    public void HiddenWindowCanBeRestoredAndOnlyExplicitExitRequestsShutdown()
    {
        var visible = true;
        var exits = 0;
        var lifetime = new DesktopLifetime(() => visible = true, () => visible = false, () => exits++);
        lifetime.EnableTray();
        Assert.True(lifetime.TryHideWindow());
        Assert.False(visible);
        Assert.Equal(0, exits);
        lifetime.ShowWindow();
        Assert.True(visible);
        lifetime.RequestExit();
        lifetime.RequestExit();
        Assert.True(lifetime.IsExiting);
        Assert.Equal(1, exits);
        Assert.False(lifetime.TryHideWindow());
    }

    [Fact]
    public void FailedTrayInitializationCannotStrandAnInvisibleProcess()
    {
        var hidden = false;
        var exits = 0;
        var lifetime = new DesktopLifetime(() => { }, () => hidden = true, () => exits++);
        Assert.False(lifetime.TryHideWindow());
        Assert.False(hidden);
        lifetime.RequestExit();
        Assert.Equal(1, exits);
    }

    [Fact]
    public void SessionEndingIsNotCancelledByCloseToTray()
    {
        var hidden = false;
        var shown = false;
        var lifetime = new DesktopLifetime(() => shown = true, () => hidden = true, () => { });
        lifetime.EnableTray();
        lifetime.PrepareSessionEnd();
        Assert.False(lifetime.TryHideWindow());
        lifetime.ShowWindow();
        Assert.False(shown);
        Assert.False(hidden);
    }
}
