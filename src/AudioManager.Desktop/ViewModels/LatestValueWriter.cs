namespace AudioManager.Desktop.ViewModels;

/// <summary>
/// 在 UI 线程上合并连续写入：同一时刻只有一个写操作，拖动期间只提交最后的值，避免请求堆积和回读覆盖拖动中的滑块。
/// </summary>
internal sealed class LatestValueWriter<T>
{
    private readonly Func<T, Task> write;
    private T pending = default!;
    private bool hasPending;
    private TaskCompletionSource? idle;
    public Task WhenIdleAsync() => idle?.Task ?? Task.CompletedTask;

    public LatestValueWriter(Func<T, Task> write) => this.write = write;

    /// <summary>正在写入或仍有待写入的值。</summary>
    public bool IsActive { get; private set; }

    /// <summary>当前写入完成后还会继续写入更新的值。</summary>
    public bool HasQueuedValue => hasPending;

    public async Task SubmitAsync(T value)
    {
        pending = value;
        hasPending = true;
        if (IsActive) return;

        IsActive = true;
        idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            while (hasPending)
            {
                var next = pending;
                hasPending = false;
                await write(next);
            }
        }
        finally { IsActive = false; idle.TrySetResult(); idle = null; }
    }
}
