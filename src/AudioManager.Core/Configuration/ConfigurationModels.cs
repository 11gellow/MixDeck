namespace AudioManager.Core.Configuration;

/// <summary>持久化文档，不含 PID、运行时会话键或实时状态对象。</summary>
public sealed record AudioConfiguration
{
    public const int CurrentVersion = 1;
    public required int Version { get; init; }
    public required SoftwareSettings Settings { get; init; }
    public required IReadOnlyList<ApplicationIdentity> KnownApplications { get; init; }
    public required IReadOnlyList<SavedDevice> KnownDevices { get; init; }
    public required IReadOnlyList<AudioProfile> Profiles { get; init; }
    public Guid? ActiveProfileId { get; init; }

    public static AudioConfiguration Empty => new()
    {
        Version = CurrentVersion, Settings = new(), KnownApplications = [], KnownDevices = [], Profiles = []
    };
}

public sealed record SoftwareSettings
{
    public bool AutomaticRestoreEnabled { get; init; }
    public bool StartAtLogin { get; init; }
    public bool ShowUnavailableDevices { get; init; }
}

public enum ApplicationIdentityKind { ExecutablePath, SystemSounds }
public sealed record ApplicationIdentity(ApplicationIdentityKind Kind, string Value);
public sealed record SavedDevice(string Id, string Name);
public sealed record SavedVolume(float Level, bool IsMuted);
public sealed record SavedAudioTarget(string DeviceId, ApplicationIdentity? Application);
public sealed record SavedVolumeEntry(SavedAudioTarget Target, string DisplayName, SavedVolume Volume);
public sealed record SavedAudioSnapshot(DateTimeOffset CapturedAt, IReadOnlyList<SavedVolumeEntry> Entries);

/// <summary>规则目标与快照分别保存；实时变化不能修改二者。</summary>
public sealed record RestoreRule(Guid Id, bool Enabled, SavedVolumeEntry Target);
public sealed record AudioProfile(Guid Id, string Name, SavedAudioSnapshot Snapshot, IReadOnlyList<RestoreRule> Rules);

public enum ConfigurationLoadKind { Missing, Loaded, BackupRecovered, Blocked }
public sealed record ConfigurationLoadResult(AudioConfiguration Configuration, ConfigurationLoadKind Kind, string? Warning);
public sealed record RestoreResult(string Target, bool Succeeded, string Message);
