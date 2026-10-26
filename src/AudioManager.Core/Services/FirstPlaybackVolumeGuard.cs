using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Models;

namespace AudioManager.Core.Services;

/// <summary>只保护已有基线的会话在首次播放前发生的一次无来源满音量重置。</summary>
internal sealed class FirstPlaybackVolumeGuard
{
    private readonly HashSet<AudioSessionKey> played = [];
    private readonly HashSet<AudioSessionKey> attempted = [];

    internal void Observe(AudioRuntimeState state)
    {
        var sessions = state.Devices.Where(device => device.IsListening && device.Device.State.HasFlag(AudioDeviceState.Active))
            .SelectMany(device => device.Sessions).Where(session => session.State != AudioSessionState.Expired).ToArray();
        var present = sessions.Select(session => session.Key).ToHashSet();
        played.RemoveWhere(key => !present.Contains(key));
        attempted.RemoveWhere(key => !present.Contains(key));
        foreach (var session in sessions.Where(session => session.State == AudioSessionState.Active)) played.Add(session.Key);
    }

    internal bool TryProtect(AudioSession session, AudioStateChanged change, SavedVolume target, SavedVolume? observed, AudioVolume actual)
    {
        if (change.Origin != AudioChangeOrigin.External || change.EventContext != Guid.Empty
            || session.State != AudioSessionState.Inactive || played.Contains(session.Key) || attempted.Contains(session.Key)
            || observed is null || target.Level >= .995f || actual.Level < .99999f || session.Volume.Level < .99999f
            || actual.IsMuted != target.IsMuted || observed.IsMuted != target.IsMuted
            || Math.Abs(observed.Level - target.Level) >= .00001f) return false;
        // 一次失败也消耗预算，后续仍由原失败保护和显式刷新处理，不能与外部程序反复争写。
        return attempted.Add(session.Key);
    }
}
