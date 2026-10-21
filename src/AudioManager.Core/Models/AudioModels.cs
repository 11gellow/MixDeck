namespace AudioManager.Core.Models;

[Flags]
public enum AudioDeviceState
{
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8
}

public enum AudioDeviceRole { Console, Multimedia, Communications }
public enum AudioSessionState { Inactive, Active, Expired }

public sealed record AudioOutputDevice(string Id, string Name, AudioDeviceState State);

/// <summary>只用于当前运行时实例；不能作为持久化应用身份。</summary>
public sealed record AudioSessionKey
{
    public string DeviceId { get; }
    public string InstanceId { get; }

    public AudioSessionKey(string deviceId, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        DeviceId = deviceId;
        InstanceId = instanceId;
    }
}

/// <summary>API 标量音量，范围为 0～1；与静音独立，不代表实际听感。</summary>
public readonly record struct AudioVolume
{
    public float Level { get; }
    public bool IsMuted { get; }

    public AudioVolume(float level, bool isMuted)
    {
        ValidateLevel(level);
        Level = level;
        IsMuted = isMuted;
    }

    public static void ValidateLevel(float level)
    {
        if (!float.IsFinite(level) || level < 0 || level > 1)
            throw new ArgumentOutOfRangeException(nameof(level), level, "音量必须是 0～1 之间的有限数值。");
    }
}

public sealed record AudioSession(
    AudioSessionKey Key,
    string? SessionIdentifier,
    uint? ProcessId,
    string Name,
    string? ProcessPath,
    bool IsSystemSounds,
    AudioSessionState State,
    AudioVolume Volume);

public sealed record AudioReadIssue(string ItemId, string Message, int NativeErrorCode);

/// <summary>空 Items 表示无可读对象；Issues 区分部分读取失败与真正的空集合。</summary>
public sealed record AudioEnumeration<T>(IReadOnlyList<T> Items, IReadOnlyList<AudioReadIssue> Issues);
