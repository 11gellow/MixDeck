using System.Runtime.InteropServices;
using AudioManager.Core.Models;
using NAudio.CoreAudioApi;

namespace AudioManager.Windows.Interop;

/// <summary>NAudio 提供查询；默认端点写入单独隔离在 Windows PolicyConfig COM 适配中。</summary>
internal sealed class PolicyConfigOutputBackend : IDefaultOutputBackend
{
    public AudioOutputDevice? GetDevice(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        return device.DataFlow == DataFlow.Render ? new(device.ID, device.FriendlyName, (AudioDeviceState)device.State) : null;
    }

    public string? GetDefault(AudioDeviceRole role)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, (Role)role, out var device)) return null;
        using (device) return device?.ID;
    }

    public void SetDefault(string deviceId, AudioDeviceRole role)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), throwOnError: true)!)!;
        try { Marshal.ThrowExceptionForHR(((IPolicyConfig)instance).SetDefaultEndpoint(deviceId, role)); }
        finally { Marshal.ReleaseComObject(instance); }
    }

    // ABI 核对：EarTrumpet IPolicyConfigWin7 和 audioswitch IPolicyConfig.h。
    // SetDefaultEndpoint 是 IUnknown 后第 11 个槽；前十槽仅保留布局，绝不调用。
    // https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/Interop/MMDeviceAPI/IPolicyConfig.cs
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int Unused1();
        [PreserveSig] int Unused2();
        [PreserveSig] int Unused3();
        [PreserveSig] int Unused4();
        [PreserveSig] int Unused5();
        [PreserveSig] int Unused6();
        [PreserveSig] int Unused7();
        [PreserveSig] int Unused8();
        [PreserveSig] int Unused9();
        [PreserveSig] int Unused10();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, AudioDeviceRole role);
    }
}
