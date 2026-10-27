using AudioManager.Core.Configuration;

namespace AudioManager.Core.Models;

public enum VolumeDiagnosticState { Matches, Differs, WaitingForSession, Unavailable, Disabled }

/// <summary>API 实际值与当前方案目标的比较，不代表听感或对修改意图的判断。</summary>
public sealed record VolumeDiagnostic(string Name, string DeviceId, AudioSessionKey? SessionKey,
    SavedVolume Target, AudioVolume? Actual, VolumeDiagnosticState State, string? Detail = null, int? NativeErrorCode = null);
