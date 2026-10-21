namespace AudioManager.Windows;

/// <summary>进程内只读标记；与数值无关，供系统原样回传以识别本进程写入。</summary>
internal static class AudioOperationContext
{
    internal static readonly Guid Id = Guid.NewGuid();
}
