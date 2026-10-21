namespace AudioManager.Desktop.ViewModels;

/// <summary>
/// 本软件连续写入后，系统会按顺序回传每次写入的事件；较早的回传快照可能晚于最后一次写入到达。
/// 写入完成后的短时间内，忽略与最后写入值不同的非外部快照，避免滑块回跳。外部修改不受此限制。
/// </summary>
internal sealed class SelfEchoGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly TimeProvider time;
    private float? lastWritten;
    private long writtenAt;

    public SelfEchoGuard(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    public void Wrote(float level)
    {
        lastWritten = level;
        writtenAt = time.GetTimestamp();
    }

    public bool IsStaleEcho(float level)
        => lastWritten is { } written
           && time.GetElapsedTime(writtenAt) < Window
           && Math.Abs(written - level) > 0.0005f;
}
