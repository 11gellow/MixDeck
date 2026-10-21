using AudioManager.Core.Models;

namespace AudioManager.Core.Events;

public enum AudioChangeOrigin { Initialization, External, Self, System, Recovery }

public enum AudioStateChangeKind
{
    Started,
    Stopped,
    DevicesChanged,
    DefaultDeviceChanged,
    DeviceListeningChanged,
    MasterVolumeChanged,
    SessionCreated,
    SessionVolumeChanged,
    SessionStateChanged,
    SessionRemoved,
    ReadFailure
}

/// <summary>携带变化来源和完成该次更新后的状态，不要求 UI 参与状态维护。</summary>
public sealed record AudioStateChanged(
    AudioStateChangeKind Kind,
    AudioChangeOrigin Origin,
    AudioRuntimeState State,
    string? DeviceId = null,
    AudioSessionKey? SessionKey = null,
    Guid? EventContext = null,
    string? Message = null,
    AudioReadIssue? Issue = null);
