using AudioManager.Core.Models;

namespace AudioManager.Windows.AudioSessions;

/// <summary>只在音频工作线程初始化/释放；回调参数必须是托管副本。</summary>
internal interface IAudioSessionSource : IDisposable
{
    AudioEnumeration<AudioSession> Initialize(Action<SessionCreatedData> onCreated);
}

internal sealed record SessionCreatedData(SessionInfoSnapshot Info, IReadOnlyList<AudioReadIssue> Issues);

internal sealed record SessionInfoSnapshot(string InstanceId, string? Identifier, uint? ProcessId,
    string DisplayName, bool IsSystemSounds, AudioSessionState State, AudioVolume Volume);
