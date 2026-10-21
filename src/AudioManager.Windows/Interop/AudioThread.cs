using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AudioManager.Windows.Interop;

/// <summary>所有 COM 对象都在同一 MTA 线程创建、使用和释放；不把 COM 对象交给调用者。</summary>
internal sealed class AudioThread : IDisposable
{
    private readonly BlockingCollection<IWorkItem> queue = new();
    private readonly object gate = new();
    private readonly Thread thread;
    private readonly Func<IDisposable> initialize;
    private bool disposed;

    internal AudioThread(Func<IDisposable>? initialize = null)
    {
        this.initialize = initialize ?? (() => new ComScope());
        thread = new Thread(Run) { IsBackground = true, Name = "AudioManager.CoreAudio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(cancellationToken);
            var item = new WorkItem<T>(action, cancellationToken);
            queue.Add(item);
            return item.Task;
        }
    }

    private void Run()
    {
        IDisposable? scope = null;
        Exception? initializationFailure = null;
        try { scope = initialize(); }
        catch (Exception exception) { initializationFailure = exception; }

        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                if (initializationFailure is not null) item.Fail(initializationFailure);
                else item.Execute();
            }
        }
        finally { scope?.Dispose(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            queue.CompleteAdding();
        }
        // 已接受的操作先完成；新请求被拒绝，随后在工作线程释放 COM 初始化。
        thread.Join();
        queue.Dispose();
    }

    private interface IWorkItem
    {
        void Execute();
        void Fail(Exception exception);
    }

    private sealed class WorkItem<T>(Func<T> action, CancellationToken token) : IWorkItem
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Task => completion.Task;

        public void Execute()
        {
            if (token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
            try { completion.TrySetResult(action()); }
            catch (Exception exception) { Fail(exception); }
        }

        public void Fail(Exception exception) => completion.TrySetException(exception);
    }

    private sealed class ComScope : IDisposable
    {
        public ComScope() => Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
        public void Dispose() => CoUninitialize();

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);
        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }
}
