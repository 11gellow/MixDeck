using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace AudioManager.Windows;

/// <summary>只读取应用主题偏好；窗口颜色仅作用于本程序窗口，不修改系统设置。</summary>
public static class WindowsThemePreference
{
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    public static void ApplyTitleBar(nint window, bool dark)
    {
        if (window == 0) return;
        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
