using AudioManager.Core.Events;
using AudioManager.Core.Models;

namespace AudioManager.Core.Interfaces;

/// <summary>独立于 UI 的全部输出设备实时状态；不读写保存配置或目标规则。</summary>
public interface IAudioStateMonitor : IDisposable
{
    Task<AudioRuntimeState> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<AudioRuntimeState> GetStateAsync(CancellationToken cancellationToken = default);
    /// <summary>主动重新枚举与重试失效监听；正常运行靠系统事件更新。</summary>
    Task<AudioRuntimeState> RefreshAsync(CancellationToken cancellationToken = default);
    /// <summary>单消费者通知队列；读取取消不停止监听，Dispose 完成队列。</summary>
    IAsyncEnumerable<AudioStateChanged> ReadChangesAsync(CancellationToken cancellationToken = default);
}
