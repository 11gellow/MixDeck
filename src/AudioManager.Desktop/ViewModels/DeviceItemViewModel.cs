using AudioManager.Core.Models;

namespace AudioManager.Desktop.ViewModels;

public sealed class DeviceItemViewModel
{
    public DeviceItemViewModel(AudioOutputDevice device, bool isDefault)
    {
        Id = device.Id;
        Name = device.Name;
        State = device.State;
        IsDefault = isDefault;
    }

    public string Id { get; }
    public string Name { get; }
    public AudioDeviceState State { get; }
    public bool IsDefault { get; }
    public bool IsActive => State.HasFlag(AudioDeviceState.Active);

    public string StateText => State switch
    {
        _ when IsActive => "可用",
        _ when State.HasFlag(AudioDeviceState.Unplugged) => "未插入",
        _ when State.HasFlag(AudioDeviceState.Disabled) => "已禁用",
        _ when State.HasFlag(AudioDeviceState.NotPresent) => "不存在",
        _ => "未知"
    };
}
