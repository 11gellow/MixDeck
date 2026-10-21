using System.Text.Json;
using AudioManager.Core.Models;
using AudioManager.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests.Integration;

/// <summary>实际桌面验收用无声双会话与独立 API 回读；不操作任何 Windows 界面。</summary>
internal static class DesktopAudioFixture
{
    internal static int Run()
    {
        using var audio = new WindowsAudioController(NullLogger<WindowsAudioController>.Instance);
        var devices = audio.EnumerateOutputDevicesAsync().GetAwaiter().GetResult().Items
            .Where(device => device.State == AudioDeviceState.Active).ToArray();
        var originals = devices.ToDictionary(device => device.Id,
            device => audio.GetMasterVolumeAsync(device.Id).GetAwaiter().GetResult());
        var selected = audio.GetDefaultOutputDeviceAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("桌面验收需要可用默认输出设备。");
        using var first = new SilentAudioSession(selected.Id);
        using var second = new SilentAudioSession(selected.Id);
        var ownSessions = audio.EnumerateSessionsAsync(selected.Id).GetAwaiter().GetResult().Items
            .Where(session => session.ProcessId == (uint)Environment.ProcessId).ToArray();
        var sessionOriginals = ownSessions.ToDictionary(session => session.Key, session => session.Volume);
        WriteState();
        try
        {
            while (Console.ReadLine() is { } command)
            {
                if (command == "stop") break;
                if (command == "restore") Restore();
                else if (command == "external")
                    audio.SetSessionVolumeAsync(ownSessions[0].Key, 0.23f).GetAwaiter().GetResult();
                WriteState();
            }
        }
        finally { Restore(); Console.WriteLine("RESTORED"); Console.Out.Flush(); }
        return 0;

        void Restore()
        {
            foreach (var (id, volume) in originals)
            {
                audio.SetMasterVolumeAsync(id, volume.Level).GetAwaiter().GetResult();
                audio.SetMasterMuteAsync(id, volume.IsMuted).GetAwaiter().GetResult();
            }
            foreach (var (key, volume) in sessionOriginals)
            {
                audio.SetSessionVolumeAsync(key, volume.Level).GetAwaiter().GetResult();
                audio.SetSessionMuteAsync(key, volume.IsMuted).GetAwaiter().GetResult();
            }
        }

        void WriteState()
        {
            var sessions = audio.EnumerateSessionsAsync(selected.Id).GetAwaiter().GetResult().Items
                .Where(session => session.ProcessId == (uint)Environment.ProcessId).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId,
                DefaultId = selected.Id,
                Devices = devices.Select(device => new { device.Id, device.Name, Volume = audio.GetMasterVolumeAsync(device.Id).GetAwaiter().GetResult() }),
                Sessions = sessions.Select(session => new { session.Key, session.Name, session.Volume })
            }));
            Console.Out.Flush();
        }
    }
}
