using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioManager.Desktop.Services;

/// <summary>按程序路径读取并缓存应用图标；读取失败返回 null，由界面显示默认图标。须在 UI 线程调用。</summary>
public sealed class AppIconProvider
{
    private readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!cache.TryGetValue(path, out var icon))
        {
            icon = Load(path);
            cache[path] = icon;
        }
        return icon;
    }

    private static ImageSource? Load(string path)
    {
        var handles = new IntPtr[1];
        try
        {
            if (ExtractIconEx(path, 0, handles, null, 1) == 0 || handles[0] == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (handles[0] != IntPtr.Zero) DestroyIcon(handles[0]);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int iconIndex, IntPtr[]? largeIcons, IntPtr[]? smallIcons, uint icons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
