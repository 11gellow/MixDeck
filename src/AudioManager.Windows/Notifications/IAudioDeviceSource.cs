using AudioManager.Core.Models;

namespace AudioManager.Windows.Notifications;

/// <summary>在音频工作线程注册及释放；系统回调只传递托管参数。</summary>
internal interface IAudioDeviceSource : IDisposable
{
    DeviceInitialState Initialize(Action<DeviceNotification> onChanged);
}

internal sealed record DeviceInitialState(AudioEnumeration<AudioOutputDevice> Devices,
    IReadOnlyDictionary<AudioDeviceRole, string?> Defaults);

internal enum DeviceNotificationKind { Added, Removed, StateChanged, DefaultChanged, PropertyChanged }

internal sealed record DeviceNotification(DeviceNotificationKind Kind, string? DeviceId,
    AudioDeviceState? State = null, AudioDeviceRole? Role = null);
