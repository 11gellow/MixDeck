using System.Diagnostics;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using Xunit.Abstractions;

namespace AudioManager.Tests.Integration;

public sealed class LiveAudioFactAttribute : FactAttribute
{
    public LiveAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUDIO_MANAGER_RUN_INTEGRATION_TESTS") != "1")
            Skip = "设置 AUDIO_MANAGER_RUN_INTEGRATION_TESTS=1 后才运行真实设备测试。";
    }
}

[Trait("Category", "Integration")]
public sealed partial class LiveAudioTests(ITestOutputHelper output)
{
    private static WindowsAudioController Create() => new(NullLogger<WindowsAudioController>.Instance);

    [LiveAudioFact]
    public async Task SessionCreationNotificationsAddNewApplicationsWithoutPollingAndStopCleanly()
    {
        using var controller = Create();
        var device = await RequireDefault(controller);
        using var fixtureThread = new AudioThread();
        var streams = new List<SilentAudioSession>();
        using var monitor = new WindowsAudioSessionMonitor(NullLogger<WindowsAudioSessionMonitor>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var events = monitor.ReadCreatedSessionsAsync(timeout.Token).GetAsyncEnumerator();
        Process? child = null;
        try
        {
            // 启动前的流由首次枚举补齐；之后不调用会话枚举寻找新会话。
            streams.Add(await fixtureThread.InvokeAsync(() => new SilentAudioSession(device.Id), default));
            var initial = await monitor.StartAsync(device.Id);
            Assert.Contains(initial.Items, session => session.ProcessId == (uint)Environment.ProcessId);
            Assert.Equal(initial.Items.Count, (await monitor.StartAsync(device.Id)).Items.Count);

            var firstRead = ReadNotificationForProcess(events, (uint)Environment.ProcessId);
            streams.Add(await fixtureThread.InvokeAsync(() => new SilentAudioSession(device.Id), default));
            var first = await firstRead;
            var secondRead = ReadNotificationForProcess(events, (uint)Environment.ProcessId);
            streams.Add(await fixtureThread.InvokeAsync(() => new SilentAudioSession(device.Id), default));
            var second = await secondRead;
            Assert.NotEqual(first.Key, second.Key);
            Assert.Equal(first.ProcessId, second.ProcessId);

            child = StartChild(device.Id);
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var third = await ReadNotificationForProcess(events, (uint)child.Id);
            Assert.NotEqual(first.ProcessId, third.ProcessId);
            var known = await monitor.GetKnownSessionsAsync(device.Id);
            foreach (var session in new[] { first, second, third })
            {
                Assert.Single(known.Items, item => item.Key == session.Key);
                Assert.DoesNotContain(initial.Items, item => item.Key == session.Key);
                Assert.False(string.IsNullOrWhiteSpace(session.Name));
                output.WriteLine($"自动创建通知：PID={session.ProcessId}；实例={session.Key.InstanceId}");
            }

            await monitor.StopAsync(device.Id);
            await monitor.StopAsync(device.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.GetKnownSessionsAsync(device.Id));
            // Stop 后继续创建本测试进程的独立流，不应再收到该进程的新增通知。
            var afterStop = ReadNotificationForProcess(events, (uint)Environment.ProcessId);
            streams.Add(await fixtureThread.InvokeAsync(() => new SilentAudioSession(device.Id), default));
            Assert.NotSame(afterStop, await Task.WhenAny(afterStop, Task.Delay(400)));
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => afterStop);

            // 显式再次开始可以枚举到停用期间的会话，不依赖自动设备重建。
            var restarted = await monitor.StartAsync(device.Id);
            Assert.True(restarted.Items.Count(session => session.ProcessId == (uint)Environment.ProcessId) >= 4);
            await monitor.StopAsync(device.Id);
            output.WriteLine("启动补齐、同 PID 多实例/另一进程增量通知、集合去重、停止后无通知及显式重启均通过；未改动任何音量。");
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        await child.StandardInput.WriteLineAsync("stop");
                        if (!child.WaitForExit(5000)) child.Kill(entireProcessTree: true);
                    }
                }
                finally { child.Dispose(); }
            }
            await fixtureThread.InvokeAsync(() =>
            {
                foreach (var stream in streams) stream.Dispose();
                return true;
            }, default);
        }
    }

    private static async Task<AudioSession> ReadNotificationForProcess(IAsyncEnumerator<AudioSessionCreated> events, uint processId)
    {
        while (await events.MoveNextAsync())
            if (events.Current.Session.ProcessId == processId) return events.Current.Session;
        throw new InvalidOperationException("创建通知流提前结束。");
    }

    [LiveAudioFact]
    public async Task DevicesAndDefaultsAgreeWithWindowsCoreAudio()
    {
        using var controller = Create();
        var result = await controller.EnumerateOutputDevicesAsync();
        Assert.NotEmpty(result.Items);
        Assert.Empty(result.Issues);
        foreach (var device in result.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            output.WriteLine($"设备：{device.Name}；状态：{device.State}；ID：{device.Id}");
        }
        using var verifierThread = new AudioThread();
        foreach (var role in Enum.GetValues<AudioDeviceRole>())
        {
            var expected = await verifierThread.InvokeAsync(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, (Role)role, out var device)) return null;
                using (device) return device?.ID;
            }, default);
            var actual = await controller.GetDefaultOutputDeviceAsync(role);
            Assert.Equal(expected, actual?.Id);
            output.WriteLine($"默认设备（{role}）：{actual?.Name ?? "无"}");
        }
    }

    [LiveAudioFact]
    public async Task SessionMetadataDiagnosticsKeepReadableSessionsAvailable()
    {
        using var controller = Create();
        var device = await RequireDefault(controller);
        var result = await controller.EnumerateSessionsAsync(device.Id);
        foreach (var issue in result.Issues)
        {
            var session = result.Items.FirstOrDefault(item => item.Key.InstanceId == issue.ItemId);
            output.WriteLine($"读取警告：应用={session?.Name ?? "未枚举"}；PID={session?.ProcessId}；路径={session?.ProcessPath ?? "不可读"}；错误={issue.Message}");
        }
        Assert.Equal(result.Items.Count, result.Items.Select(item => item.Key).Distinct().Count());
        foreach (var session in result.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(session.Key.InstanceId));
            Assert.InRange(session.Volume.Level, 0f, 1f);
        }
        output.WriteLine($"只读枚举：{result.Items.Count} 个会话，{result.Issues.Count} 项警告；未写入音量或配置。");
    }

    [LiveAudioFact]
    public async Task MasterVolumeCanBeWrittenReadBackAndRestored()
    {
        using var controller = Create();
        var device = await RequireDefault(controller);
        var original = await controller.GetMasterVolumeAsync(device.Id);
        var target = original.Level >= 0.05f ? original.Level - 0.05f : original.Level + 0.05f;
        try
        {
            var changed = await controller.SetMasterVolumeAsync(device.Id, target);
            Assert.InRange(Math.Abs(changed.Level - target), 0f, 0.02f);
            Assert.NotEqual(original.Level, changed.Level);
            Assert.Equal(original.IsMuted, changed.IsMuted);
            Assert.True((await controller.SetMasterMuteAsync(device.Id, true)).IsMuted);
            var readBack = await controller.GetMasterVolumeAsync(device.Id);
            Assert.InRange(Math.Abs(readBack.Level - target), 0f, 0.02f);
            Assert.True(readBack.IsMuted);
            output.WriteLine($"主音量：原值 {original.Level}，目标 {target}，实际 {changed.Level}；静音设置及回读通过。");
        }
        finally
        {
            try { await controller.SetMasterVolumeAsync(device.Id, original.Level); }
            finally { await controller.SetMasterMuteAsync(device.Id, original.IsMuted); }
        }
        var restored = await controller.GetMasterVolumeAsync(device.Id);
        Assert.InRange(Math.Abs(restored.Level - original.Level), 0f, 0.02f);
        Assert.Equal(original.IsMuted, restored.IsMuted);
        output.WriteLine($"主音量恢复：{restored.Level}；静音恢复：{restored.IsMuted}。");
    }

    [LiveAudioFact]
    public async Task MultipleSessionsAndApplicationsAreControlledIndependently()
    {
        using var controller = Create();
        var device = await RequireDefault(controller);
        using var fixtureThread = new AudioThread();
        SilentAudioSession? firstStream = null;
        SilentAudioSession? secondStream = null;
        Process? child = null;
        var originals = new Dictionary<AudioSessionKey, AudioVolume>();
        try
        {
            var before = (await controller.EnumerateSessionsAsync(device.Id)).Items.Select(session => session.Key).ToHashSet();
            firstStream = await fixtureThread.InvokeAsync(() => new SilentAudioSession(device.Id), default);
            var first = await FindNewSession(controller, device.Id, before, (uint)Environment.ProcessId);
            before.Add(first.Key);
            secondStream = await fixtureThread.InvokeAsync(() => new SilentAudioSession(device.Id), default);
            var second = await FindNewSession(controller, device.Id, before, (uint)Environment.ProcessId);
            Assert.Equal(first.ProcessId, second.ProcessId);
            Assert.NotEqual(first.Key, second.Key);

            child = StartChild(device.Id);
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(ready == "READY", $"无声子进程初始化失败：{ready}");
            var third = await FindNewSession(controller, device.Id, [], (uint)child.Id);
            Assert.NotEqual(first.ProcessId, third.ProcessId);
            foreach (var session in new[] { first, second, third })
            {
                originals[session.Key] = await controller.GetSessionVolumeAsync(session.Key);
                Assert.False(string.IsNullOrWhiteSpace(session.Name));
                Assert.False(string.IsNullOrWhiteSpace(session.SessionIdentifier));
                Assert.False(string.IsNullOrWhiteSpace(session.ProcessPath));
                output.WriteLine($"测试会话：PID={session.ProcessId}；应用={session.Name}；实例={session.Key.InstanceId}");
            }
            foreach (var level in new[] { 0f, 0.37f, 1f })
            {
                AssertNear(level, (await controller.SetSessionVolumeAsync(first.Key, level)).Level);
                Assert.Equal(originals[second.Key], await controller.GetSessionVolumeAsync(second.Key));
                Assert.Equal(originals[third.Key], await controller.GetSessionVolumeAsync(third.Key));
            }
            Assert.True((await controller.SetSessionMuteAsync(first.Key, true)).IsMuted);
            Assert.Equal(originals[second.Key], await controller.GetSessionVolumeAsync(second.Key));
            Assert.Equal(originals[third.Key], await controller.GetSessionVolumeAsync(third.Key));
            AssertNear(0.23f, (await controller.SetSessionVolumeAsync(third.Key, 0.23f)).Level);
            AssertNear(originals[second.Key].Level, (await controller.GetSessionVolumeAsync(second.Key)).Level);
            Assert.True((await controller.GetSessionVolumeAsync(first.Key)).IsMuted);
            output.WriteLine("同进程两个会话与另一进程会话的独立音量、静音、边界值及回读检查通过。");
        }
        finally
        {
            try
            {
                foreach (var (key, volume) in originals)
                {
                    try { await controller.SetSessionVolumeAsync(key, volume.Level); }
                    finally { await controller.SetSessionMuteAsync(key, volume.IsMuted); }
                    Assert.Equal(volume, await controller.GetSessionVolumeAsync(key));
                }
            }
            finally
            {
                if (child is not null)
                {
                    try
                    {
                        if (!child.HasExited)
                        {
                            await child.StandardInput.WriteLineAsync("stop");
                            if (!child.WaitForExit(5000)) child.Kill(entireProcessTree: true);
                        }
                    }
                    finally { child.Dispose(); }
                }
                await fixtureThread.InvokeAsync(() =>
                {
                    try { firstStream?.Dispose(); }
                    finally { secondStream?.Dispose(); }
                    return true;
                }, default);
            }
        }
    }

    [LiveAudioFact]
    public async Task MissingObjectsAndRepeatedReleaseAreHandled()
    {
        using var controller = Create();
        var device = await RequireDefault(controller);
        var missingDevice = await Assert.ThrowsAsync<AudioControlException>(() => controller.GetMasterVolumeAsync("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"));
        Assert.Equal(AudioErrorCode.DeviceUnavailable, missingDevice.Code);
        var missingSession = await Assert.ThrowsAsync<AudioControlException>(() => controller.GetSessionVolumeAsync(new(device.Id, Guid.NewGuid().ToString())));
        Assert.Equal(AudioErrorCode.SessionUnavailable, missingSession.Code);
        for (var iteration = 0; iteration < 20; iteration++)
        {
            using var temporary = Create();
            Assert.NotEmpty((await temporary.EnumerateOutputDevicesAsync()).Items);
            _ = await temporary.EnumerateSessionsAsync(device.Id);
            _ = await temporary.GetMasterVolumeAsync(device.Id);
        }
        output.WriteLine("无效设备、无效会话及 20 次控制器创建/读取/释放检查通过。");
    }

    [LiveAudioFact]
    public async Task ExitedApplicationSessionBecomesUnavailable()
    {
        using var controller = Create();
        var device = await RequireDefault(controller);
        using var child = StartChild(device.Id);
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var session = await FindNewSession(controller, device.Id, [], (uint)child.Id);
            await child.StandardInput.WriteLineAsync("stop");
            Assert.True(child.WaitForExit(5000));
            Assert.Equal(0, child.ExitCode);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var current = await controller.EnumerateSessionsAsync(device.Id);
                if (current.Items.All(item => item.Key != session.Key))
                {
                    var error = await Assert.ThrowsAsync<AudioControlException>(() => controller.GetSessionVolumeAsync(session.Key));
                    Assert.Equal(AudioErrorCode.SessionUnavailable, error.Code);
                    output.WriteLine("应用退出后会话从枚举中移除；旧实例读取返回 SessionUnavailable，无未处理异常。");
                    return;
                }
                await Task.Delay(100);
            }
            Assert.Fail("应用退出后会话仍未失效。");
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
    }

    private static async Task<AudioOutputDevice> RequireDefault(IAudioController controller)
    {
        var device = await controller.GetDefaultOutputDeviceAsync();
        Assert.True(device is not null, "真实设备测试需要可用的默认音频输出设备。");
        return device;
    }

    private static void AssertNear(float expected, float actual) => Assert.InRange(Math.Abs(expected - actual), 0f, 0.001f);

    private static async Task<AudioSession> FindNewSession(IAudioController controller, string deviceId, HashSet<AudioSessionKey> previous, uint processId)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var sessions = await controller.EnumerateSessionsAsync(deviceId);
            var found = sessions.Items.Where(session => session.ProcessId == processId && !previous.Contains(session.Key)).ToArray();
            if (found.Length == 1) return found[0];
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"未找到 PID={processId} 新创建的无声会话。");
    }

    private static Process StartChild(string deviceId)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(typeof(LiveAudioTests).Assembly.Location);
        info.ArgumentList.Add("--silent-session");
        info.ArgumentList.Add(deviceId);
        return Process.Start(info) ?? throw new InvalidOperationException("无法创建无声测试子进程。");
    }
}
