using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace AudioManager.Windows;

public sealed class WindowsAudioController : IAudioController
{
    private readonly IAudioBackend backend;
    private readonly AudioThread audioThread;
    private readonly ILogger<WindowsAudioController> logger;

    public WindowsAudioController(ILogger<WindowsAudioController> logger)
        : this(new NAudioBackend(logger), logger, new AudioThread()) { }

    internal WindowsAudioController(IAudioBackend backend, ILogger<WindowsAudioController> logger, AudioThread audioThread)
    {
        this.backend = backend;
        this.logger = logger;
        this.audioThread = audioThread;
    }

    public Task<AudioEnumeration<AudioOutputDevice>> EnumerateOutputDevicesAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("枚举输出设备", backend.EnumerateOutputDevices, cancellationToken);

    public Task<AudioOutputDevice?> GetDefaultOutputDeviceAsync(AudioDeviceRole role = AudioDeviceRole.Multimedia, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        return InvokeAsync("读取默认输出设备", () => backend.GetDefaultOutputDevice(role), cancellationToken);
    }

    public Task<AudioVolume> GetMasterVolumeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return InvokeAsync("读取系统主音量", () => backend.GetMasterVolume(deviceId), cancellationToken);
    }

    public Task<AudioVolume> SetMasterVolumeAsync(string deviceId, float level, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        AudioVolume.ValidateLevel(level);
        return InvokeAsync("设置系统主音量", () => backend.SetMasterVolume(deviceId, level), cancellationToken);
    }

    public Task<AudioVolume> SetMasterMuteAsync(string deviceId, bool isMuted, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return InvokeAsync("设置系统静音", () => backend.SetMasterMute(deviceId, isMuted), cancellationToken);
    }

    public Task<AudioEnumeration<AudioSession>> EnumerateSessionsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return InvokeAsync("枚举应用会话", () => backend.EnumerateSessions(deviceId), cancellationToken);
    }

    public Task<AudioVolume> GetSessionVolumeAsync(AudioSessionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return InvokeAsync("读取会话音量", () => backend.GetSessionVolume(key), cancellationToken);
    }

    public Task<AudioVolume> SetSessionVolumeAsync(AudioSessionKey key, float level, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        AudioVolume.ValidateLevel(level);
        return InvokeAsync("设置会话音量", () => backend.SetSessionVolume(key, level), cancellationToken);
    }

    public Task<AudioVolume> SetSessionMuteAsync(AudioSessionKey key, bool isMuted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return InvokeAsync("设置会话静音", () => backend.SetSessionMute(key, isMuted), cancellationToken);
    }

    private async Task<T> InvokeAsync<T>(string operation, Func<T> action, CancellationToken cancellationToken)
    {
        try { return await audioThread.InvokeAsync(action, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException and not ObjectDisposedException)
        {
            logger.LogError(exception, "音频操作失败：{Operation}", operation);
            if (exception is AudioControlException) throw;
            var code = exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005)
                ? AudioErrorCode.AccessDenied : AudioErrorCode.ApiFailure;
            throw new AudioControlException(code, operation, $"{operation}失败，请重新获取设备或会话后重试。", exception);
        }
    }

    public void Dispose() => audioThread.Dispose();
}
