using AudioManager.Core.Models;

namespace AudioManager.Windows.Interop;

/// <summary>仅补齐 NAudio 会话 setter 未暴露的 EventContext；调用者已在音频 MTA 线程。</summary>
internal static class SessionVolumeWriter
{
    internal static AudioVolume SetVolume(AudioSessionKey key, float level, Guid context)
    {
        AudioVolume.ValidateLevel(level);
        return WithSession(key, session => { session.SetVolume(level, context); return session.Volume; });
    }

    internal static AudioVolume SetMute(AudioSessionKey key, bool mute, Guid context)
        => WithSession(key, session => { session.SetMute(mute, context); return session.Volume; });

    private static AudioVolume WithSession(AudioSessionKey key, Func<NativeSession, AudioVolume> action)
    {
        using var manager = SessionComInterop.OpenManager(key.DeviceId);
        using var sessions = manager.Enumerate();
        var count = sessions.Count;
        for (var index = 0; index < count; index++)
        {
            NativeSession session;
            try { session = sessions.GetSession(index); }
            catch { continue; } // 非目标项可能已经退出，不妨碍继续寻找精确实例。
            using (session)
            {
                string instanceId;
                try { instanceId = session.InstanceId; }
                catch { continue; }
                if (!string.Equals(instanceId, key.InstanceId, StringComparison.Ordinal)) continue;
                // 找到目标后的状态读取和写入失败必须保留原异常。
                if (session.State == AudioSessionState.Expired) break;
                return action(session);
            }
        }
        throw new AudioControlException(AudioErrorCode.SessionUnavailable, "获取指定会话", "会话已退出或失效，请重新枚举。");
    }
}
