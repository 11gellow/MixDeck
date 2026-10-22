using AudioManager.Core.Events;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests.Integration;

public sealed partial class LiveAudioTests
{
    [LiveAudioFact]
    public async Task SwitchingSystemDefaultOutputChangesActualRolesAndNotifiesMonitorThenRestoresOriginals()
    {
        using var reader = Create();
        var defaults = new Dictionary<AudioDeviceRole, string?>();
        foreach (var role in Enum.GetValues<AudioDeviceRole>()) defaults[role] = (await reader.GetDefaultOutputDeviceAsync(role))?.Id;
        Assert.NotNull(defaults[AudioDeviceRole.Console]);
        Assert.NotNull(defaults[AudioDeviceRole.Multimedia]);
        var devices = (await reader.EnumerateOutputDevicesAsync()).Items.Where(device => device.State.HasFlag(AudioDeviceState.Active)).ToArray();
        var target = devices.FirstOrDefault(device => device.Id != defaults[AudioDeviceRole.Multimedia])
            ?? throw new Xunit.Sdk.XunitException("默认设备切换实机测试需要两个可用输出端点。");
        using var switcher = new WindowsDefaultAudioOutputController(NullLogger<WindowsDefaultAudioOutputController>.Instance);
        using var monitor = new WindowsAudioStateMonitor(NullLogger<WindowsAudioStateMonitor>.Instance);
        using var native = new AudioThread();
        await monitor.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var changes = monitor.ReadChangesAsync(timeout.Token).GetAsyncEnumerator();
        try
        {
            await switcher.SetDefaultOutputDeviceAsync(target.Id);
            Assert.Equal(target.Id, (await reader.GetDefaultOutputDeviceAsync(AudioDeviceRole.Console))?.Id);
            Assert.Equal(target.Id, (await reader.GetDefaultOutputDeviceAsync(AudioDeviceRole.Multimedia))?.Id);
            Assert.Equal(defaults[AudioDeviceRole.Communications], (await reader.GetDefaultOutputDeviceAsync(AudioDeviceRole.Communications))?.Id);
            var notification = await ReadStateChange(changes, change => change.Kind == AudioStateChangeKind.DefaultDeviceChanged
                && change.State.DefaultOutputDevices.GetValueOrDefault(AudioDeviceRole.Multimedia) == target.Id);
            Assert.Equal(target.Id, notification.State.DefaultOutputDevices[AudioDeviceRole.Multimedia]);
            output.WriteLine($"真实切换至 {target.Name}：独立 Console/Multimedia 回读一致，通信默认不变，收到真实默认设备通知（角色 {notification.Message}，完整状态含多媒体新设备）。");
        }
        finally
        {
            // 两种原角色可能不同，逐角色还原，不能用一次双角色切换误覆盖原设置。
            foreach (var role in new[] { AudioDeviceRole.Console, AudioDeviceRole.Multimedia })
            {
                var id = defaults[role]!;
                await native.InvokeAsync(() => { new PolicyConfigOutputBackend().SetDefault(id, role); return true; }, default);
                Assert.Equal(id, (await reader.GetDefaultOutputDeviceAsync(role))?.Id);
            }
            Assert.Equal(defaults[AudioDeviceRole.Communications], (await reader.GetDefaultOutputDeviceAsync(AudioDeviceRole.Communications))?.Id);
            output.WriteLine("finally 已分别恢复原 Console/Multimedia 输出并独立回读；未调用音量或静音写入。");
        }
    }
}
