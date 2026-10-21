using System.Diagnostics;
using System.Threading.Channels;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.Interop;
using AudioManager.Windows.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class NativeEndpointSourceTests
{
    [LiveAudioFact]
    public async Task SessionEventsPreserveExactWriteContextAndExpiredSessionsAreReleased()
    {
        using var controller = new WindowsAudioController(NullLogger<WindowsAudioController>.Instance);
        var device = await controller.GetDefaultOutputDeviceAsync();
        Assert.NotNull(device);
        using var worker = new AudioThread();
        var events = Channel.CreateUnbounded<EndpointNotification>();
        var source = new NAudioEndpointSource(device.Id, NullLogger.Instance, action =>
        {
            _ = worker.InvokeAsync(() => { action(); return true; }, default);
            return true;
        });
        Process? child = null;
        try
        {
            await worker.InvokeAsync(() => source.Initialize(notification => events.Writer.TryWrite(notification)), default);
            child = StartChild(device.Id);
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var created = await Read<EndpointSessionCreated>(events.Reader,
                item => item.Data.Info.ProcessId == (uint)child.Id);
            var key = new AudioSessionKey(device.Id, created.Data.Info.InstanceId);
            var selfWrite = await controller.SetSessionVolumeAsync(key, 0.37f);
            Assert.Equal(0.37f, selfWrite.Level);
            var ownEvent = await Read<EndpointSessionVolumeChanged>(events.Reader,
                item => item.InstanceId == key.InstanceId && item.Volume.Level == 0.37f);
            Assert.Equal(AudioOperationContext.Id, ownEvent.EventContext);

            var externalContext = Guid.NewGuid();
            await worker.InvokeAsync(() => SessionVolumeWriter.SetVolume(key, 0.23f, externalContext), default);
            var externalEvent = await Read<EndpointSessionVolumeChanged>(events.Reader,
                item => item.InstanceId == key.InstanceId && item.Volume.Level == 0.23f);
            Assert.Equal(externalContext, externalEvent.EventContext);
            await controller.SetSessionMuteAsync(key, true);
            var muteEvent = await Read<EndpointSessionVolumeChanged>(events.Reader,
                item => item.InstanceId == key.InstanceId && item.Volume.IsMuted);
            Assert.Equal(AudioOperationContext.Id, muteEvent.EventContext);

            await child.StandardInput.WriteLineAsync("stop");
            Assert.True(child.WaitForExit(5000));
            Assert.Equal(0, child.ExitCode);
            await Read<EndpointSessionRemoved>(events.Reader, item => item.InstanceId == key.InstanceId);
        }
        finally
        {
            if (child is not null)
            {
                try { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
                finally { child.Dispose(); }
            }
            await worker.InvokeAsync(() => { source.Dispose(); source.Dispose(); return true; }, default);
        }
    }

    private static async Task<T> Read<T>(ChannelReader<EndpointNotification> reader, Func<T, bool> predicate) where T : EndpointNotification
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var item in reader.ReadAllAsync(timeout.Token))
            if (item is T value && predicate(value)) return value;
        throw new InvalidOperationException("本机音频事件流提前结束。");
    }

    private static Process StartChild(string deviceId)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(NativeEndpointSourceTests).Assembly.Location);
        start.ArgumentList.Add("--silent-session");
        start.ArgumentList.Add(deviceId);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动无声测试子进程。");
    }
}
