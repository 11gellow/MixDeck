using System.Diagnostics;
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
    public async Task PersistedConfigurationRestoresAcrossNewProcessAndAppliesToLateSessionAndExternalChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MixDeck-live-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var controller = Create();
        var device = await RequireDefault(controller);
        using var firstChild = StartChild(device.Id);
        Process? secondChild = null;
        var originals = new Dictionary<AudioSessionKey, AudioVolume>();
        try
        {
            Assert.Equal("READY", await firstChild.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var first = await FindNewSession(controller, device.Id, [], (uint)firstChild.Id);
            originals[first.Key] = first.Volume;
            await controller.SetSessionVolumeAsync(first.Key, .37f);
            await controller.SetSessionMuteAsync(first.Key, true);
            first = first with { Volume = new(.37f, true) };
            var state = new AudioRuntimeState(1, DateTimeOffset.UtcNow, true,
                [new(device, null, true, [first], [])], new Dictionary<AudioDeviceRole, string?>(), []);
            var path = Path.Combine(directory, "settings.json");
            using (var savedService = new AudioConfigurationService(new JsonConfigurationStore(path), controller))
            {
                await savedService.InitializeAsync();
                await savedService.SaveSnapshotAsync(state, "真实测试会话");
            }
            var savedJson = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("processId", savedJson);
            await controller.SetSessionVolumeAsync(first.Key, .8f);
            await controller.SetSessionMuteAsync(first.Key, false);

            var info = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add(typeof(LiveAudioTests).Assembly.Location);
            info.ArgumentList.Add("--configuration-restore-probe");
            info.ArgumentList.Add(path);
            info.ArgumentList.Add(firstChild.Id.ToString());
            using (var probe = Process.Start(info)!)
            {
                try
                {
                    await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(0, probe.ExitCode);
                    Assert.Contains("RESTORE_COMPLETED", await probe.StandardOutput.ReadToEndAsync());
                }
                finally { if (!probe.HasExited) { probe.Kill(true); probe.WaitForExit(5000); } }
            }
            var restored = await controller.GetSessionVolumeAsync(first.Key);
            AssertNear(.37f, restored.Level);
            Assert.True(restored.IsMuted);
            output.WriteLine("JSON 保存后独立新进程加载并恢复真实测试会话：0.8/false → 0.37/true；未写系统主音量。");

            using var monitor = new WindowsAudioStateMonitor(NullLogger<WindowsAudioStateMonitor>.Instance);
            using var guard = new AudioWriteGuard(controller);
            using var service = new AudioConfigurationService(new JsonConfigurationStore(path), guard);
            await service.InitializeAsync();
            await monitor.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var changes = monitor.ReadChangesAsync(timeout.Token).GetAsyncEnumerator();
            secondChild = StartChild(device.Id);
            Assert.Equal("READY", await secondChild.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var second = await FindNewSession(controller, device.Id, [], (uint)secondChild.Id);
            originals[second.Key] = second.Volume;
            Assert.NotEqual(first.ProcessId, second.ProcessId);
            Assert.NotEqual(first.Key, second.Key);
            var created = await ReadStateChange(changes, change => change.Kind == AudioStateChangeKind.SessionCreated && change.SessionKey == second.Key);
            var filtered = Filter(created.State, firstChild.Id, secondChild.Id);
            await service.HandleStateAsync(filtered, created with { State = filtered });
            restored = await controller.GetSessionVolumeAsync(second.Key);
            AssertNear(.37f, restored.Level);
            Assert.True(restored.IsMuted);

            using var native = new AudioThread();
            var context = Guid.NewGuid();
            await native.InvokeAsync(() => SessionVolumeWriter.SetVolume(second.Key, .23f, context), default);
            var external = await ReadStateChange(changes, change => change.Kind == AudioStateChangeKind.SessionVolumeChanged
                && change.SessionKey == second.Key && change.EventContext == context);
            filtered = Filter(external.State, firstChild.Id, secondChild.Id);
            await service.HandleStateAsync(filtered, external with { State = filtered });
            AssertNear(.23f, (await controller.GetSessionVolumeAsync(second.Key)).Level);
            AssertNear(.23f, (await controller.GetSessionVolumeAsync(first.Key)).Level);
            var persisted = ConfigurationJson.Deserialize(await File.ReadAllTextAsync(path));
            AssertNear(.23f, persisted.Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
            AssertNear(.37f, persisted.Profiles[0].Snapshot.Entries.Single().Volume.Level);

            var disabledContext = Guid.NewGuid();
            await native.InvokeAsync(() => SessionVolumeWriter.SetVolume(second.Key, .19f, disabledContext), default);
            var disabled = await ReadStateChange(changes, change => change.SessionKey == second.Key && change.EventContext == disabledContext);
            filtered = Filter(disabled.State, firstChild.Id, secondChild.Id);
            await service.HandleStateAsync(filtered, disabled with { State = filtered });
            AssertNear(.19f, (await controller.GetSessionVolumeAsync(second.Key)).Level);
            AssertNear(.19f, (await controller.GetSessionVolumeAsync(first.Key)).Level);
            AssertNear(.19f, ConfigurationJson.Deserialize(await File.ReadAllTextAsync(path)).Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
            output.WriteLine("新 PID/实例迟启动会话自动 0.37/true；真实外部 0.23 → 自动保存并统一所有会话；再次调节 0.19 保持并保存；原始快照不变。");

            var edit = await service.BeginVolumeEditAsync(device.Id, second.Key);
            var beforeDrag = await File.ReadAllTextAsync(path);
            foreach (var level in new[] { .31f, .47f })
            {
                var dragContext = Guid.NewGuid();
                await native.InvokeAsync(() => SessionVolumeWriter.SetVolume(second.Key, level, dragContext), default);
                var change = await ReadStateChange(changes, change => change.SessionKey == second.Key && change.EventContext == dragContext);
                filtered = Filter(change.State, firstChild.Id, secondChild.Id);
                await service.HandleStateAsync(filtered, change with { State = filtered });
                AssertNear(level, (await controller.GetSessionVolumeAsync(second.Key)).Level);
                Assert.Equal(beforeDrag, await File.ReadAllTextAsync(path));
            }
            await service.EndVolumeEditAsync(edit);
            AssertNear(.47f, ConfigurationJson.Deserialize(await File.ReadAllTextAsync(path)).Profiles[0].Rules.Single(rule => rule.Target.Target.Application is not null).Target.Volume.Level);
            AssertNear(.47f, (await controller.GetSessionVolumeAsync(first.Key)).Level);
            output.WriteLine("真实音量及回调 0.31 → 0.47 在编辑范围内实时生效，磁盘保持原样；结束编辑后保存最终 0.47 并统一同路径会话。");
        }
        finally
        {
            foreach (var pair in originals)
            {
                await controller.SetSessionVolumeAsync(pair.Key, pair.Value.Level);
                await controller.SetSessionMuteAsync(pair.Key, pair.Value.IsMuted);
                Assert.Equal(pair.Value, await controller.GetSessionVolumeAsync(pair.Key));
            }
            foreach (var child in new[] { firstChild, secondChild }.OfType<Process>())
            {
                if (!child.HasExited) { await child.StandardInput.WriteLineAsync("stop"); if (!child.WaitForExit(5000)) child.Kill(true); }
            }
            secondChild?.Dispose();
            Directory.Delete(directory, true);
            output.WriteLine("仅写入本测试的无声会话，finally 已逐项恢复原值并回读；临时配置和子进程已清理。");
        }
    }

    private static AudioRuntimeState Filter(AudioRuntimeState state, params int[] pids)
        => state with { Devices = state.Devices.Select(device => device with
        {
            MasterVolume = null, Sessions = device.Sessions.Where(session => session.ProcessId is { } pid && pids.Contains((int)pid)).ToArray()
        }).ToArray() };
}
