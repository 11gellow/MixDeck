namespace AudioManager.Core.Configuration;

/// <summary>持久化文档，不含 PID、运行时会话键或实时状态对象。</summary>
public sealed record AudioConfiguration
{
    public const int CurrentVersion = 2;
    public required int Version { get; init; }
    public required SoftwareSettings Settings { get; init; }
    public required IReadOnlyList<ApplicationIdentity> KnownApplications { get; init; }
    public required IReadOnlyList<SavedDevice> KnownDevices { get; init; }
    public required IReadOnlyList<AudioProfile> Profiles { get; init; }
    public required IReadOnlyList<DeviceProfileSelection> DeviceProfiles { get; init; }
    public Guid? ActiveProfileId { get; init; }

    public static AudioConfiguration Empty => new()
    {
        Version = CurrentVersion, Settings = new(), KnownApplications = [], KnownDevices = [], Profiles = [], DeviceProfiles = []
    };
}

public sealed record SoftwareSettings
{
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

/// <summary>用户调节更新规则目标；手动快照保持原值，恢复回调不回写规则。</summary>
public sealed record RestoreRule(Guid Id, bool Enabled, SavedVolumeEntry Target);
public sealed record AudioProfile(Guid Id, string Name, SavedAudioSnapshot Snapshot, IReadOnlyList<RestoreRule> Rules);
public sealed record DeviceProfileSelection(string DeviceId, Guid ProfileId);

public enum ConfigurationLoadKind { Missing, Loaded, BackupRecovered, Blocked }
public sealed record ConfigurationLoadResult(AudioConfiguration Configuration, ConfigurationLoadKind Kind, string? Warning);
public sealed record RestoreResult(string Target, bool Succeeded, string Message);
