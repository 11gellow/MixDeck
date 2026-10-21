using System.Collections.Concurrent;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;

namespace AudioManager.Core.Services;

/// <summary>按实际写入/回读结果限制控制；不从硬件支持位猜测混合器是否可编辑。底层由 DI 容器持有。</summary>
public sealed class AudioWriteGuard(IAudioController inner) : IAudioController
{
    private readonly ConcurrentDictionary<string, string> restrictions = new(StringComparer.Ordinal);
    public event Action? Changed;
    public static string MasterKey(string device) => "master:" + device;
    public static string SessionKey(AudioSessionKey key) => "session:" + key.DeviceId + "\n" + key.InstanceId;
    public string? Restriction(string key) => restrictions.GetValueOrDefault(key);
    public void ClearRestrictions() { restrictions.Clear(); Changed?.Invoke(); }
    public void ForgetSession(AudioSessionKey key) { if (restrictions.TryRemove(SessionKey(key), out _)) Changed?.Invoke(); }
    public void ForgetDevice(string deviceId)
    {
        foreach (var key in restrictions.Keys.Where(key => key == MasterKey(deviceId) || key.StartsWith("session:" + deviceId + "\n", StringComparison.Ordinal)))
            restrictions.TryRemove(key, out _);
        Changed?.Invoke();
    }

    private async Task<AudioVolume> WriteAsync(string key, Func<Task<AudioVolume>> write, Func<Task<AudioVolume>> read,
        Func<AudioVolume, bool> accepted)
    {
        if (Restriction(key) is { } reason) throw new InvalidOperationException("当前无法修改：" + reason + "；可点击刷新后重试。");
        try
        {
            await write();
            var actual = await read();
            if (!accepted(actual))
            {
                restrictions[key] = "系统回读值未达到请求值，已显示实际状态";
                Changed?.Invoke();
            }
            return actual;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ArgumentException)
        {
            restrictions[key] = exception.Message;
            Changed?.Invoke();
            throw;
        }
    }

    public Task<AudioVolume> SetMasterVolumeAsync(string deviceId, float level, CancellationToken cancellationToken = default)
    {
        AudioVolume.ValidateLevel(level);
        return WriteAsync(MasterKey(deviceId), () => inner.SetMasterVolumeAsync(deviceId, level, cancellationToken),
            () => inner.GetMasterVolumeAsync(deviceId, cancellationToken), actual => Math.Abs(actual.Level - level) <= .005f);
    }
    public Task<AudioVolume> SetMasterMuteAsync(string deviceId, bool isMuted, CancellationToken cancellationToken = default)
        => WriteAsync(MasterKey(deviceId), () => inner.SetMasterMuteAsync(deviceId, isMuted, cancellationToken),
            () => inner.GetMasterVolumeAsync(deviceId, cancellationToken), actual => actual.IsMuted == isMuted);
    public Task<AudioVolume> SetSessionVolumeAsync(AudioSessionKey key, float level, CancellationToken cancellationToken = default)
    {
        AudioVolume.ValidateLevel(level);
        return WriteAsync(SessionKey(key), () => inner.SetSessionVolumeAsync(key, level, cancellationToken),
            () => inner.GetSessionVolumeAsync(key, cancellationToken), actual => Math.Abs(actual.Level - level) <= .005f);
    }
    public Task<AudioVolume> SetSessionMuteAsync(AudioSessionKey key, bool isMuted, CancellationToken cancellationToken = default)
        => WriteAsync(SessionKey(key), () => inner.SetSessionMuteAsync(key, isMuted, cancellationToken),
            () => inner.GetSessionVolumeAsync(key, cancellationToken), actual => actual.IsMuted == isMuted);
    public Task<AudioEnumeration<AudioOutputDevice>> EnumerateOutputDevicesAsync(CancellationToken cancellationToken = default) => inner.EnumerateOutputDevicesAsync(cancellationToken);
    public Task<AudioOutputDevice?> GetDefaultOutputDeviceAsync(AudioDeviceRole role = AudioDeviceRole.Multimedia, CancellationToken cancellationToken = default) => inner.GetDefaultOutputDeviceAsync(role, cancellationToken);
    public Task<AudioVolume> GetMasterVolumeAsync(string deviceId, CancellationToken cancellationToken = default) => inner.GetMasterVolumeAsync(deviceId, cancellationToken);
    public Task<AudioEnumeration<AudioSession>> EnumerateSessionsAsync(string deviceId, CancellationToken cancellationToken = default) => inner.EnumerateSessionsAsync(deviceId, cancellationToken);
    public Task<AudioVolume> GetSessionVolumeAsync(AudioSessionKey key, CancellationToken cancellationToken = default) => inner.GetSessionVolumeAsync(key, cancellationToken);
    public void Dispose() { } // 不拥有 inner。
}
