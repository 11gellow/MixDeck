using AudioManager.Core.Events;
using AudioManager.Core.Models;

namespace AudioManager.Core.Interfaces;

/// <summary>按输出设备监听会话创建；不负责音量变化、退出清理或设备自动重建。</summary>
public interface IAudioSessionMonitor : IDisposable
{
    /// <summary>注册并读取已有会话；对已监听设备重复调用不会重复注册。</summary>
    Task<AudioEnumeration<AudioSession>> StartAsync(string deviceId, CancellationToken cancellationToken = default);
    Task StopAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>已有会话与创建通知的去重集合；数据为创建时快照，并非实时音量状态。</summary>
    Task<AudioEnumeration<AudioSession>> GetKnownSessionsAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 单消费者队列，只包含新增项，不重复发送启动枚举项。通知可在读取前暂存；
    /// 取消读取不停止监听，Stop 前已入队的通知仍可读取。Dispose 完成队列。
    /// </summary>
    IAsyncEnumerable<AudioSessionCreated> ReadCreatedSessionsAsync(CancellationToken cancellationToken = default);
}
