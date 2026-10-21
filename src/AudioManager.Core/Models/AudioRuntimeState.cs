namespace AudioManager.Core.Models;

/// <summary>单个输出设备的当前可观察状态；不包含已保存或规则目标音量。</summary>
public sealed record AudioDeviceRuntimeState(
    AudioOutputDevice Device,
    AudioVolume? MasterVolume,
    bool IsListening,
    IReadOnlyList<AudioSession> Sessions,
    IReadOnlyList<AudioReadIssue> Issues);

/// <summary>实时状态的只读快照。Revision 只在内容发生变化时递增。</summary>
public sealed record AudioRuntimeState(
    long Revision,
    DateTimeOffset CapturedAt,
    bool IsRunning,
    IReadOnlyList<AudioDeviceRuntimeState> Devices,
    IReadOnlyDictionary<AudioDeviceRole, string?> DefaultOutputDevices,
    IReadOnlyList<AudioReadIssue> Issues);
