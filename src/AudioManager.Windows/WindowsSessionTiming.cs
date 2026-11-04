using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioManager.Windows;

/// <summary>只读查询当前交互会话的登录时间；不可用时明确返回 null。</summary>
public static class WindowsSessionTiming
{
    public static DateTimeOffset? GetLogonTime()
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, Process.GetCurrentProcess().SessionId, 24, out var buffer, out var bytes)) return null;
        try
        {
            if (bytes < Marshal.SizeOf<WtsInfo>()) return null;
            var info = Marshal.PtrToStructure<WtsInfo>(buffer);
            return info.LogonTime > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(info.LogonTime)) : null;
        }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfo
    {
        public int State, SessionId;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }
    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("Wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
