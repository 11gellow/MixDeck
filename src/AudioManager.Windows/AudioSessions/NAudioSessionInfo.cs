using AudioManager.Core.Models;
using NAudio.CoreAudioApi;

namespace AudioManager.Windows.AudioSessions;

internal sealed class NAudioSessionInfo(AudioSessionControl session) : ISessionInfo
{
    public string InstanceId => session.GetSessionInstanceIdentifier;
    public string Identifier => session.GetSessionIdentifier;
    public uint ProcessId => session.GetProcessID;
    public string DisplayName => session.DisplayName;
    public bool IsSystemSounds => session.IsSystemSoundsSession;
    public AudioSessionState State => (AudioSessionState)session.State;
    public AudioVolume Volume => new(session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute);
}
