using AudioManager.Core.Models;
using AudioManager.Tests.Integration;
using AudioManager.Windows.Interop;
using AudioManager.Windows.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;

namespace AudioManager.Tests;

[Trait("Category", "Integration")]
public sealed class DeviceSourceTests
{
    [LiveAudioFact]
    public async Task RegisterReadAndReleaseDeviceNotificationsWithoutChangingSystemDevices()
    {
        using var worker = new AudioThread();
        await worker.InvokeAsync(() =>
        {
            for (var iteration = 0; iteration < 10; iteration++)
            {
                using var source = new NAudioDeviceSource(NullLogger.Instance);
                var initial = source.Initialize(_ => { });
                Assert.Empty(initial.Devices.Issues);
                Assert.Equal(initial.Devices.Items.Count, initial.Devices.Items.Select(item => item.Id).Distinct().Count());
                Assert.Throws<InvalidOperationException>(() => source.Initialize(_ => { }));

                // 独立查询系统配置，不创建、切换或移除任何真实设备。
                using var verifier = new MMDeviceEnumerator();
                using var devices = verifier.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
                var expected = new List<(string Id, AudioDeviceState State)>();
                for (var index = 0; index < devices.Count; index++)
                {
                    using var device = devices[index];
                    expected.Add((device.ID, (AudioDeviceState)device.State));
                }
                Assert.Equal(expected.OrderBy(item => item.Id),
                    initial.Devices.Items.Select(item => (item.Id, item.State)).OrderBy(item => item.Id));
                Assert.Equal(3, initial.Defaults.Count);
                foreach (var role in Enum.GetValues<AudioDeviceRole>())
                {
                    string? expectedDefault = null;
                    if (verifier.TryGetDefaultAudioEndpoint(DataFlow.Render, (Role)role, out var device))
                    {
                        using (device)
                            if (device?.State == DeviceState.Active) expectedDefault = device.ID;
                    }
                    Assert.Equal(expectedDefault, initial.Defaults[role]);
                }
                source.Dispose();
                source.Dispose();
                Assert.Throws<ObjectDisposedException>(() => source.Initialize(_ => { }));
            }
            return true;
        }, default);
    }
}
