using AudioManager.Core.Events;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;

namespace AudioManager.Tests.Integration;

// 与原有主音量集成测试属于同一 xUnit 类，避免同时写入系统主音量。
public sealed partial class LiveAudioTests
{
    [LiveAudioFact]
    public async Task RealtimeStateTracksNativeEventsAndExactOriginsWithoutPolling()
    {
        using var controller = Create();
        var device = await RequireDefault(controller);
        using var monitor = new WindowsAudioStateMonitor(NullLogger<WindowsAudioStateMonitor>.Instance);
        using var nativeThread = new AudioThread();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var events = monitor.ReadChangesAsync(timeout.Token).GetAsyncEnumerator();
        var initial = await monitor.StartAsync();
        Assert.True(initial.IsRunning);
        Assert.Equal(device.Id, initial.DefaultOutputDevices[AudioDeviceRole.Multimedia]);
        Assert.All(initial.Devices.Where(item => item.Device.State == AudioDeviceState.Active), item => Assert.True(item.IsListening));
        var original = await controller.GetMasterVolumeAsync(device.Id);
        using var child = StartChild(device.Id);
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var created = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.SessionCreated
                && item.State.Devices.SelectMany(endpoint => endpoint.Sessions).Any(session => session.ProcessId == (uint)child.Id));
            var key = Assert.Single(created.State.Devices.SelectMany(endpoint => endpoint.Sessions), session => session.ProcessId == (uint)child.Id).Key;
            Assert.DoesNotContain(initial.Devices.SelectMany(endpoint => endpoint.Sessions), session => session.Key == key);

            await controller.SetSessionVolumeAsync(key, 0.37f);
            var self = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.SessionVolumeChanged
                && item.SessionKey == key && item.Origin == AudioChangeOrigin.Self);
            Assert.Equal(AudioOperationContext.Id, self.EventContext);
            AssertNear(0.37f, SessionFrom(self.State, key).Volume.Level);

            var externalContext = Guid.NewGuid();
            await nativeThread.InvokeAsync(() => SessionVolumeWriter.SetVolume(key, 0.23f, externalContext), default);
            var external = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.SessionVolumeChanged
                && item.SessionKey == key && item.EventContext == externalContext);
            Assert.Equal(AudioChangeOrigin.External, external.Origin);
            AssertNear(0.23f, SessionFrom(external.State, key).Volume.Level);

            await controller.SetSessionMuteAsync(key, true);
            var mute = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.SessionVolumeChanged
                && item.SessionKey == key && SessionFrom(item.State, key).Volume.IsMuted);
            Assert.Equal(AudioChangeOrigin.Self, mute.Origin);
            Assert.True(SessionFrom(mute.State, key).Volume.IsMuted);

            await child.StandardInput.WriteLineAsync("pause");
            Assert.Equal("PAUSED", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var inactive = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.SessionStateChanged
                && item.SessionKey == key && SessionFrom(item.State, key).State == AudioSessionState.Inactive);
            Assert.Equal(AudioChangeOrigin.System, inactive.Origin);
            await child.StandardInput.WriteLineAsync("resume");
            Assert.Equal("RESUMED", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var active = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.SessionStateChanged
                && item.SessionKey == key && SessionFrom(item.State, key).State == AudioSessionState.Active);
            Assert.Equal(AudioChangeOrigin.System, active.Origin);

            var target = original.Level >= 0.05f ? original.Level - 0.05f : original.Level + 0.05f;
            await controller.SetMasterVolumeAsync(device.Id, target);
            var master = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.MasterVolumeChanged
                && item.DeviceId == device.Id && item.Origin == AudioChangeOrigin.Self);
            Assert.InRange(Math.Abs(MasterFrom(master.State, device.Id).Level - target), 0f, 0.02f);
            await controller.SetMasterMuteAsync(device.Id, !original.IsMuted);
            var masterMute = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.MasterVolumeChanged
                && item.DeviceId == device.Id && MasterFrom(item.State, device.Id).IsMuted != original.IsMuted);
            Assert.Equal(AudioChangeOrigin.Self, masterMute.Origin);

            // 独立模拟外部调用方，真实系统回调须保留与本进程标记不同的 context。
            await nativeThread.InvokeAsync(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                using var endpoint = enumerator.GetDevice(device.Id);
                endpoint.AudioEndpointVolume.NotificationGuid = externalContext;
                endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = original.Level;
                endpoint.AudioEndpointVolume.Mute = original.IsMuted;
                return true;
            }, default);
            var externalMaster = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.MasterVolumeChanged
                && item.DeviceId == device.Id && item.EventContext == externalContext
                && MasterFrom(item.State, device.Id).IsMuted == original.IsMuted
                && Math.Abs(MasterFrom(item.State, device.Id).Level - original.Level) < 0.02f);
            Assert.Equal(AudioChangeOrigin.External, externalMaster.Origin);

            await child.StandardInput.WriteLineAsync("stop");
            Assert.True(child.WaitForExit(5000));
            Assert.Equal(0, child.ExitCode);
            var removed = await ReadStateChange(events, item => item.Kind == AudioStateChangeKind.SessionRemoved && item.SessionKey == key);
            Assert.DoesNotContain(removed.State.Devices.SelectMany(endpoint => endpoint.Sessions), session => session.Key == key);
            var final = await monitor.GetStateAsync();
            Assert.DoesNotContain(final.Devices.SelectMany(endpoint => endpoint.Sessions), session => session.Key == key);
            Assert.True(final.Revision > initial.Revision);
            output.WriteLine($"实时状态：默认设备 {device.Name}；创建/独立会话音量/静音/Inactive→Active/退出均由本机事件更新；主音量和会话 self/external context 精确区分；Revision {initial.Revision}→{final.Revision}；无会话轮询。");
        }
        finally
        {
            try
            {
                await controller.SetMasterVolumeAsync(device.Id, original.Level);
                await controller.SetMasterMuteAsync(device.Id, original.IsMuted);
                var restored = await controller.GetMasterVolumeAsync(device.Id);
                Assert.InRange(Math.Abs(restored.Level - original.Level), 0f, 0.02f);
                Assert.Equal(original.IsMuted, restored.IsMuted);
                output.WriteLine($"实时集成主音量恢复：{restored.Level}；静音={restored.IsMuted}。");
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
                await monitor.StopAsync();
                Assert.False((await monitor.GetStateAsync()).IsRunning);
            }
        }
    }

    private static AudioSession SessionFrom(AudioRuntimeState state, AudioSessionKey key)
        => Assert.Single(state.Devices.SelectMany(endpoint => endpoint.Sessions), session => session.Key == key);

    private static AudioVolume MasterFrom(AudioRuntimeState state, string deviceId)
        => Assert.Single(state.Devices, endpoint => endpoint.Device.Id == deviceId).MasterVolume!.Value;

    private static async Task<AudioStateChanged> ReadStateChange(IAsyncEnumerator<AudioStateChanged> events, Func<AudioStateChanged, bool> predicate)
    {
        while (await events.MoveNextAsync())
            if (predicate(events.Current)) return events.Current;
        throw new InvalidOperationException("实时状态流提前结束。");
    }
}
