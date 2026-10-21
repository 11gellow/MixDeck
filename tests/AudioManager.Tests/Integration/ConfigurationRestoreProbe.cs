using AudioManager.Core.Services;
using AudioManager.Core.Models;
using AudioManager.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests.Integration;

internal static class ConfigurationRestoreProbe
{
    internal static int Run(string path, uint processId)
    {
        try { return RunAsync(path, processId).GetAwaiter().GetResult(); }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static async Task<int> RunAsync(string path, uint processId)
    {
        using var raw = new WindowsAudioController(NullLogger<WindowsAudioController>.Instance);
        using var guard = new AudioWriteGuard(raw);
        using var service = new AudioConfigurationService(new JsonConfigurationStore(path), guard);
        using var monitor = new WindowsAudioStateMonitor(NullLogger<WindowsAudioStateMonitor>.Instance);
        await service.InitializeAsync();
        if (service.Current.Profiles.SelectMany(profile => profile.Rules).Any(rule => rule.Target.Target.Application is null))
            throw new InvalidOperationException("测试探针不能包含系统主音量目标。");
        var state = await monitor.StartAsync();
        state = state with { Devices = state.Devices.Select(device => device with
        {
            MasterVolume = null, Sessions = device.Sessions.Where(session => session.ProcessId == processId).ToArray()
        }).ToArray() };
        var failures = new List<string>();
        service.Reported += result => { if (!result.Succeeded) failures.Add(result.Message); };
        await service.HandleStateAsync(state);
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(";", failures));
        Console.WriteLine("RESTORE_COMPLETED");
        return 0;
    }
}
