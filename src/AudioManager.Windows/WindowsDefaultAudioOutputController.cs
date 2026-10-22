using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace AudioManager.Windows;

/// <summary>在专用 MTA 上切换普通播放角色，回读确认；部分失败时尽力回退，不修改通信角色。</summary>
public sealed class WindowsDefaultAudioOutputController : IDefaultAudioOutputController
{
    private static readonly AudioDeviceRole[] PlaybackRoles = [AudioDeviceRole.Console, AudioDeviceRole.Multimedia];
    private readonly IDefaultOutputBackend backend;
    private readonly AudioThread thread;
    private readonly ILogger<WindowsDefaultAudioOutputController> logger;

    public WindowsDefaultAudioOutputController(ILogger<WindowsDefaultAudioOutputController> logger)
        : this(new PolicyConfigOutputBackend(), logger, new AudioThread()) { }

    internal WindowsDefaultAudioOutputController(IDefaultOutputBackend backend, ILogger<WindowsDefaultAudioOutputController> logger, AudioThread thread)
    { this.backend = backend; this.logger = logger; this.thread = thread; }

    public async Task SetDefaultOutputDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        try
        {
            await thread.InvokeAsync(() => { Switch(deviceId); return true; }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ObjectDisposedException)
        {
            logger.LogError(exception, "切换 Windows 默认输出失败：{DeviceId}", deviceId);
            if (exception is AudioControlException) throw;
            throw new AudioControlException(IsDenied(exception) ? AudioErrorCode.AccessDenied : AudioErrorCode.ApiFailure,
                "切换默认输出设备", "Windows 未能切换输出设备，请刷新后重试。", exception);
        }
    }

    private void Switch(string deviceId)
    {
        var device = backend.GetDevice(deviceId);
        if (device is null || !device.State.HasFlag(AudioDeviceState.Active))
            throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "切换默认输出设备", "输出设备不可用或已断开，请选择可用设备。");
        var originals = PlaybackRoles.ToDictionary(role => role, backend.GetDefault);
        if (originals.Values.All(id => SameDevice(id, deviceId))) return;
        try
        {
            foreach (var role in PlaybackRoles)
                if (!SameDevice(originals[role], deviceId)) backend.SetDefault(deviceId, role);
            foreach (var role in PlaybackRoles)
                if (!SameDevice(backend.GetDefault(role), deviceId))
                    throw new InvalidOperationException("Windows 回读的默认输出未达到请求设备。");
        }
        catch (Exception exception)
        {
            var rollbackFailures = new List<string>();
            foreach (var role in PlaybackRoles.Reverse())
            {
                try
                {
                    // 只撤回自己的改动，不覆盖并发发生的第三方设备选择。
                    if (!SameDevice(originals[role], deviceId) && SameDevice(backend.GetDefault(role), deviceId))
                    {
                        if (originals[role] is not { } previous) { rollbackFailures.Add(role + " 原先无默认设备"); continue; }
                        backend.SetDefault(previous, role);
                        if (!SameDevice(backend.GetDefault(role), previous)) rollbackFailures.Add(role + " 回退未生效");
                    }
                }
                catch (Exception failure) { rollbackFailures.Add(role + "：" + failure.Message); }
            }
            throw new AudioControlException(IsDenied(exception) ? AudioErrorCode.AccessDenied : AudioErrorCode.ApiFailure,
                "切换默认输出设备", "输出切换未完成，已尝试恢复原设备。"
                    + (rollbackFailures.Count == 0 ? "请以系统回读状态为准。" : "部分回退失败：" + string.Join("；", rollbackFailures)), exception);
        }
    }

    private static bool SameDevice(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool IsDenied(Exception exception) => exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005);
    public void Dispose() => thread.Dispose();
}

internal interface IDefaultOutputBackend
{
    AudioOutputDevice? GetDevice(string deviceId);
    string? GetDefault(AudioDeviceRole role);
    void SetDefault(string deviceId, AudioDeviceRole role);
}
