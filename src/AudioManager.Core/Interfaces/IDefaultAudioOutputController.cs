namespace AudioManager.Core.Interfaces;

/// <summary>切换系统普通播放输出（Console/Multimedia），完成后已回读核对；通信设备独立。</summary>
public interface IDefaultAudioOutputController : IDisposable
{
    Task SetDefaultOutputDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
}
