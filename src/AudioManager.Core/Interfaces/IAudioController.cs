using AudioManager.Core.Models;

namespace AudioManager.Core.Interfaces;

/// <summary>异步音频控制入口。无默认设备时返回 null；失效对象与 API 失败抛出 AudioControlException。</summary>
public interface IAudioController : IDisposable
{
    Task<AudioEnumeration<AudioOutputDevice>> EnumerateOutputDevicesAsync(CancellationToken cancellationToken = default);
    Task<AudioOutputDevice?> GetDefaultOutputDeviceAsync(AudioDeviceRole role = AudioDeviceRole.Multimedia, CancellationToken cancellationToken = default);
    Task<AudioVolume> GetMasterVolumeAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<AudioVolume> SetMasterVolumeAsync(string deviceId, float level, CancellationToken cancellationToken = default);
    Task<AudioVolume> SetMasterMuteAsync(string deviceId, bool isMuted, CancellationToken cancellationToken = default);
    Task<AudioEnumeration<AudioSession>> EnumerateSessionsAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<AudioVolume> GetSessionVolumeAsync(AudioSessionKey key, CancellationToken cancellationToken = default);
    Task<AudioVolume> SetSessionVolumeAsync(AudioSessionKey key, float level, CancellationToken cancellationToken = default);
    Task<AudioVolume> SetSessionMuteAsync(AudioSessionKey key, bool isMuted, CancellationToken cancellationToken = default);
}
