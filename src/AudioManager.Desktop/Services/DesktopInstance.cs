using System.Diagnostics;
using System.Security.Principal;

namespace AudioManager.Desktop.Services;

/// <summary>按用户及交互会话隔离，避免任务与手动启动重复创建音频消费者。</summary>
internal sealed class DesktopInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle show;
    private RegisteredWaitHandle? wait;
    public bool IsPrimary { get; }
    internal DesktopInstance(string? name = null)
    {
        name ??= $"Local\\MixDeck-{WindowsIdentity.GetCurrent().User!.Value}-{Process.GetCurrentProcess().SessionId}";
        mutex = new Mutex(true, name, out var created);
        IsPrimary = created;
        show = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Show");
    }
    public void Listen(Action action) => wait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => action(), null, Timeout.Infinite, false);
    public void RequestShow() => show.Set();
    public void Dispose()
    {
        wait?.Unregister(null);
        show.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
