using AudioManager.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AudioManager.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsAudio(this IServiceCollection services)
    {
        services.TryAddSingleton<IAudioController, WindowsAudioController>();
        services.TryAddSingleton<IAudioSessionMonitor, WindowsAudioSessionMonitor>();
        services.TryAddSingleton<IAudioStateMonitor, WindowsAudioStateMonitor>();
        return services;
    }
}
