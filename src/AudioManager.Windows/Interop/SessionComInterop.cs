using System.Runtime.InteropServices;
using AudioManager.Core.Models;
using AudioManager.Windows.AudioSessions;

namespace AudioManager.Windows.Interop;

/// <summary>
/// NAudio 3.2.0 的会话封装未暴露写入/回调 EventContext；此窄投影仅补齐会话身份、
/// volume 与 events，所有 RCW 单独持有并在 MTA 释放，不访问 NAudio 私有成员。
/// 方法顺序依据 Windows SDK audiopolicy.h / audioclient.h / mmdeviceapi.h。
/// </summary>
internal static class SessionComInterop
{
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal static NativeSessionManager OpenManager(string deviceId)
    {
        var enumeratorId = typeof(INativeDeviceEnumerator).GUID;
        Check(CoCreateInstance(in EnumeratorClass, IntPtr.Zero, 1, in enumeratorId, out var pointer));
        var enumerator = FromOwnedPointer<INativeDeviceEnumerator>(pointer);
        INativeDevice? device = null;
        try
        {
            Check(enumerator.GetDevice(deviceId, out pointer));
            device = FromOwnedPointer<INativeDevice>(pointer);
            Check(device.GetState(out var state));
            Check(((INativeEndpoint)device).GetDataFlow(out var dataFlow));
            if (state != 1 || dataFlow != 0)
                throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "获取会话监听设备", "该输出设备当前不可用。");
            var managerId = typeof(INativeSessionManager).GUID;
            Check(device.Activate(in managerId, 23, IntPtr.Zero, out pointer));
            return new NativeSessionManager(FromOwnedPointer<INativeSessionManager>(pointer));
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
        { throw new AudioControlException(AudioErrorCode.DeviceUnavailable, "获取会话监听设备", "输出设备不存在。", exception); }
        finally { Release(device); Release(enumerator); }
    }

    // 传入指针必须拥有一次引用；RCW 取得独立引用后释放该次所有权。
    internal static T FromOwnedPointer<T>(IntPtr pointer) where T : class
    {
        object? wrapper = null;
        try
        {
            wrapper = Marshal.GetUniqueObjectForIUnknown(pointer);
            return (T)wrapper;
        }
        catch { Release(wrapper); throw; }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    }

    internal static void Release(object? wrapper)
    {
        if (wrapper is not null && Marshal.IsComObject(wrapper)) Marshal.FinalReleaseComObject(wrapper);
    }

    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    internal static Guid ReadContext(IntPtr context) => context == IntPtr.Zero ? Guid.Empty : Marshal.PtrToStructure<Guid>(context);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);
}

internal sealed class NativeSessionManager(INativeSessionManager manager) : IDisposable
{
    internal INativeSessionManager Interface { get; } = manager;

    internal NativeSessionEnumerator Enumerate()
    {
        SessionComInterop.Check(Interface.GetSessionEnumerator(out var pointer));
        return new NativeSessionEnumerator(SessionComInterop.FromOwnedPointer<INativeSessionEnumerator>(pointer));
    }

    public void Dispose() => SessionComInterop.Release(Interface);
}

internal sealed class NativeSessionEnumerator(INativeSessionEnumerator enumerator) : IDisposable
{
    internal int Count
    {
        get { SessionComInterop.Check(enumerator.GetCount(out var count)); return count; }
    }

    internal NativeSession GetSession(int index)
    {
        SessionComInterop.Check(enumerator.GetSession(index, out var pointer));
        return NativeSession.FromOwnedPointer(pointer);
    }

    public void Dispose() => SessionComInterop.Release(enumerator);
}

internal sealed class NativeSession : ISessionInfo, IDisposable
{
    private readonly INativeSessionControl control;
    private readonly INativeSimpleAudioVolume volume;
    private bool disposed;

    private NativeSession(INativeSessionControl control)
    {
        this.control = control;
        volume = (INativeSimpleAudioVolume)control;
    }

    internal static NativeSession FromOwnedPointer(IntPtr pointer)
    {
        var control = SessionComInterop.FromOwnedPointer<INativeSessionControl>(pointer);
        try { return new(control); }
        catch { SessionComInterop.Release(control); throw; }
    }

    public string InstanceId { get { SessionComInterop.Check(control.GetSessionInstanceIdentifier(out var value)); return value; } }
    public string Identifier { get { SessionComInterop.Check(control.GetSessionIdentifier(out var value)); return value; } }
    public uint ProcessId { get { SessionComInterop.Check(control.GetProcessId(out var value)); return value; } }
    public string DisplayName { get { SessionComInterop.Check(control.GetDisplayName(out var value)); return value; } }
    public bool IsSystemSounds
    {
        get
        {
            var result = control.IsSystemSoundsSession();
            SessionComInterop.Check(result);
            return result == 0;
        }
    }
    public AudioSessionState State { get { SessionComInterop.Check(control.GetState(out var value)); return (AudioSessionState)value; } }
    public AudioVolume Volume
    {
        get
        {
            SessionComInterop.Check(volume.GetMasterVolume(out var level));
            SessionComInterop.Check(volume.GetMute(out var muted));
            return new(level, muted);
        }
    }

    internal void Register(INativeSessionEvents callback) => SessionComInterop.Check(control.RegisterAudioSessionNotification(callback));
    internal void Unregister(INativeSessionEvents callback) => SessionComInterop.Check(control.UnregisterAudioSessionNotification(callback));
    internal void SetVolume(float level, Guid context) => SessionComInterop.Check(volume.SetMasterVolume(level, in context));
    internal void SetMute(bool muted, Guid context) => SessionComInterop.Check(volume.SetMute(muted, in context));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SessionComInterop.Release(control);
    }
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeDevice
{
    [PreserveSig] int Activate(in Guid iid, uint context, IntPtr activationParameters, out IntPtr instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr propertyStore);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeEndpoint
{
    [PreserveSig] int GetDataFlow(out int dataFlow);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeSessionManager
{
    [PreserveSig] int GetAudioSessionControl(in Guid sessionId, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(in Guid sessionId, uint flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IntPtr sessions);
    [PreserveSig] int RegisterSessionNotification(INativeSessionNotification callback);
    [PreserveSig] int UnregisterSessionNotification(INativeSessionNotification callback);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr callback);
    [PreserveSig] int UnregisterDuckNotification(IntPtr callback);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IntPtr session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, in Guid context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, in Guid context);
    [PreserveSig] int GetGroupingParam(out Guid grouping);
    [PreserveSig] int SetGroupingParam(in Guid grouping, in Guid context);
    [PreserveSig] int RegisterAudioSessionNotification(INativeSessionEvents callback);
    [PreserveSig] int UnregisterAudioSessionNotification(INativeSessionEvents callback);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeSimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, in Guid context);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComVisible(true), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeSessionNotification
{
    [PreserveSig] int OnSessionCreated(IntPtr session);
}

[ComVisible(true), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
    [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
    [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
    [PreserveSig] int OnChannelVolumeChanged(uint channels, IntPtr volumes, uint changedChannel, IntPtr context);
    [PreserveSig] int OnGroupingParamChanged(IntPtr grouping, IntPtr context);
    [PreserveSig] int OnStateChanged(int state);
    [PreserveSig] int OnSessionDisconnected(int reason);
}
