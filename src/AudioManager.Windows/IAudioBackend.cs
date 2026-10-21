using AudioManager.Core.Models;

namespace AudioManager.Windows;

internal interface IAudioBackend
{
    AudioEnumeration<AudioOutputDevice> EnumerateOutputDevices();
    AudioOutputDevice? GetDefaultOutputDevice(AudioDeviceRole role);
    AudioVolume GetMasterVolume(string deviceId);
    AudioVolume SetMasterVolume(string deviceId, float level);
    AudioVolume SetMasterMute(string deviceId, bool isMuted);
    AudioEnumeration<AudioSession> EnumerateSessions(string deviceId);
    AudioVolume GetSessionVolume(AudioSessionKey key);
    AudioVolume SetSessionVolume(AudioSessionKey key, float level);
    AudioVolume SetSessionMute(AudioSessionKey key, bool isMuted);
}
