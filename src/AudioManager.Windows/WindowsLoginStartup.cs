using AudioManager.Core.Interfaces;
using Microsoft.Win32;

namespace AudioManager.Windows;

/// <summary>仅管理当前用户的 MixDeck Run 项；不要求管理员权限。</summary>
public sealed class WindowsLoginStartup(string executablePath, string valueName = "MixDeck", string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run") : ILoginStartup
{
    public string Command { get; } = BuildCommand(executablePath);
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, false);
            // 旧安装路径或缺失托盘参数不能证明当前程序已正确注册。
            return key?.GetValue(valueName) is string command
                && string.Equals(command.Trim(), Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKey, true);
        if (enabled) key.SetValue(valueName, Command, RegistryValueKind.String);
        else key.DeleteValue(valueName, false);
        if (IsEnabled != enabled) throw new IOException("无法验证登录启动设置。");
    }

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.IndexOfAny(['"', '\r', '\n']) >= 0
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("登录启动需要可执行文件的完整路径。", nameof(executablePath));
        var command = $"\"{executablePath}\" --tray";
        if (command.Length > 260) throw new ArgumentException("登录启动命令超过 Windows Run 项 260 字符限制。", nameof(executablePath));
        return command;
    }
}
