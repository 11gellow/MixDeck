using System.IO;
using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Models;
using AudioManager.Core.Services;
using AudioManager.Windows;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests.Integration;

public sealed partial class LiveAudioTests
{
    [LiveAudioFact]
    public async Task SameValueAndOffsetRewritesAndManualTargetsUseOnlyIndependentSilentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MixDeck-reapply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var controller = Create();
        var device = await RequireDefault(controller);
        using var child = StartChild(device.Id);
        AudioSession? session = null;
        using var monitor = new WindowsAudioStateMonitor(NullLogger<WindowsAudioStateMonitor>.Instance);
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            session = await FindNewSession(controller, device.Id, [], (uint)child.Id);
            await controller.SetSessionVolumeAsync(session.Key, .37f);
            var before = await controller.GetSessionVolumeAsync(session.Key);
            await controller.SetSessionVolumeAsync(session.Key, before.Level);
            Assert.Equal(before, await controller.GetSessionVolumeAsync(session.Key));
            await controller.SetSessionVolumeAsync(session.Key, .38f);
            AssertNear(.38f, (await controller.GetSessionVolumeAsync(session.Key)).Level);
            await controller.SetSessionVolumeAsync(session.Key, before.Level);
            Assert.Equal(before, await controller.GetSessionVolumeAsync(session.Key));
            var state = Filter(await monitor.StartAsync(), child.Id);
            var path = Path.Combine(directory, "settings.json");
            using var service = new AudioConfigurationService(new JsonConfigurationStore(path), controller);
            await service.InitializeAsync();
            await service.SaveSnapshotAsync(state, "重应用实验");
            await service.HandleStateAsync(state);
            var persisted = await File.ReadAllTextAsync(path);
            await controller.SetSessionVolumeAsync(session.Key, .9f);
            Assert.Contains(await service.InspectAsync(state, device.Id), item => item.State == VolumeDiagnosticState.Differs && item.Actual is { } actual && actual.Level > .89f);
            AssertNear(.9f, (await controller.GetSessionVolumeAsync(session.Key)).Level);
            await service.ReapplyTargetsAsync(state, device.Id);
            Assert.Equal(before, await controller.GetSessionVolumeAsync(session.Key));
            Assert.Equal(persisted, await File.ReadAllTextAsync(path));
            output.WriteLine("独立无声会话：0.37 同值重写回读一致，0.37→0.38→0.37 成功且静音不变；检查准确发现实际 0.9/目标 0.37，手动重应用恢复 0.37，配置不变。该测试不能测听感，未验证同值重写能改善听感。");
        }
        finally
        {
            await monitor.StopAsync();
            if (session is not null)
            {
                await controller.SetSessionVolumeAsync(session.Key, session.Volume.Level);
                await controller.SetSessionMuteAsync(session.Key, session.Volume.IsMuted);
                Assert.Equal(session.Volume, await controller.GetSessionVolumeAsync(session.Key));
            }
            if (!child.HasExited) { await child.StandardInput.WriteLineAsync("stop"); if (!child.WaitForExit(5000)) { child.Kill(true); child.WaitForExit(5000); } }
            Directory.Delete(directory, true);
        }
    }

    [LiveAudioFact]
    public async Task NativeFirstPlaybackResetRestoresSavedLevelAndLaterExternalControlStillSaves()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MixDeck-first-playback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var controller = Create();
        var device = await RequireDefault(controller);
        using var child = StartChild(device.Id);
        AudioSession? session = null;
        using var monitor = new WindowsAudioStateMonitor(NullLogger<WindowsAudioStateMonitor>.Instance);
        using var native = new AudioThread();
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            session = await FindNewSession(controller, device.Id, [], (uint)child.Id);
            await child.StandardInput.WriteLineAsync("pause");
            Assert.Equal("PAUSED", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await controller.SetSessionVolumeAsync(session.Key, .1f);
            var initial = Filter(await monitor.StartAsync(), child.Id);
            Assert.Equal(AudioSessionState.Inactive, SessionFrom(initial, session.Key).State);
            var path = Path.Combine(directory, "settings.json");
            using var service = new AudioConfigurationService(new JsonConfigurationStore(path), controller);
            await service.InitializeAsync();
            await service.SaveSnapshotAsync(initial, "首次播放测试");
            await service.HandleStateAsync(initial);
            var saved = await File.ReadAllTextAsync(path);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var changes = monitor.ReadChangesAsync(timeout.Token).GetAsyncEnumerator();

            await native.InvokeAsync(() => SessionVolumeWriter.SetVolume(session.Key, 1, Guid.Empty), default);
            var reset = await ReadStateChange(changes, change => change.Kind == AudioStateChangeKind.SessionVolumeChanged
                && change.SessionKey == session.Key && change.EventContext == Guid.Empty);
            Assert.Equal(AudioSessionState.Inactive, SessionFrom(reset.State, session.Key).State);
            var filtered = Filter(reset.State, child.Id);
            await service.HandleStateAsync(filtered, reset with { State = filtered });
            AssertNear(.1f, (await controller.GetSessionVolumeAsync(session.Key)).Level);
            Assert.Equal(saved, await File.ReadAllTextAsync(path));
            var echo = await ReadStateChange(changes, change => change.SessionKey == session.Key && change.EventContext == AudioOperationContext.Id);
            filtered = Filter(echo.State, child.Id);
            await service.HandleStateAsync(filtered, echo with { State = filtered });
            Assert.Equal(saved, await File.ReadAllTextAsync(path));

            await child.StandardInput.WriteLineAsync("resume");
            Assert.Equal("RESUMED", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var active = await ReadStateChange(changes, change => change.Kind == AudioStateChangeKind.SessionStateChanged
                && change.SessionKey == session.Key && SessionFrom(change.State, session.Key).State == AudioSessionState.Active);
            filtered = Filter(active.State, child.Id);
            await service.HandleStateAsync(filtered, active with { State = filtered });
            await native.InvokeAsync(() => SessionVolumeWriter.SetVolume(session.Key, 1, Guid.Empty), default);
            var ordinary = await ReadStateChange(changes, change => change.Kind == AudioStateChangeKind.SessionVolumeChanged
                && change.SessionKey == session.Key && change.EventContext == Guid.Empty);
            filtered = Filter(ordinary.State, child.Id);
            await service.HandleStateAsync(filtered, ordinary with { State = filtered });
            AssertNear(1, (await controller.GetSessionVolumeAsync(session.Key)).Level);
            AssertNear(1, ConfigurationJson.Deserialize(await File.ReadAllTextAsync(path)).Profiles[0].Rules.Single().Target.Volume.Level);
            output.WriteLine("原生无声会话：首次空闲 GUID_NULL 0.1→1 被恢复至 0.1，配置不变、Self 回调不循环；播放后 GUID_NULL 调节至 1 正常生效并保存。");
        }
        finally
        {
            await monitor.StopAsync();
            if (session is not null)
            {
                await controller.SetSessionVolumeAsync(session.Key, session.Volume.Level);
                await controller.SetSessionMuteAsync(session.Key, session.Volume.IsMuted);
                Assert.Equal(session.Volume, await controller.GetSessionVolumeAsync(session.Key));
            }
            if (!child.HasExited)
            {
                await child.StandardInput.WriteLineAsync("stop");
                if (!child.WaitForExit(5000)) { child.Kill(true); child.WaitForExit(5000); }
            }
            Directory.Delete(directory, true);
            output.WriteLine("仅修改本测试独立无声会话，原音量/静音已恢复并回读；未写用户配置或系统主音量。");
        }
    }
}
