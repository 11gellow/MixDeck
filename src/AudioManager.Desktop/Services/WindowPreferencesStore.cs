using System.IO;
using System.Text.Json;

namespace AudioManager.Desktop.Services;

public enum WindowCloseBehavior { Ask, Exit, Tray }

/// <summary>窗口交互偏好独立保存，不改音量或外观配置格式。</summary>
public sealed class WindowPreferencesStore
{
    private readonly string path;
    public WindowCloseBehavior CloseBehavior { get; private set; } = WindowCloseBehavior.Ask;
    public string? Error { get; private set; }
    public event Action? Changed;
    public WindowPreferencesStore(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("窗口偏好文件过大。");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var mode = root.GetProperty("closeBehavior").GetString();
            if (root.GetProperty("version").GetInt32() != 1 || !Enum.TryParse<WindowCloseBehavior>(mode, out var value)
                || !Enum.IsDefined(value) || mode != value.ToString()) throw new InvalidDataException("不支持的窗口偏好格式。");
            CloseBehavior = value;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        { Error = "窗口偏好读取失败，关闭时将再次询问：" + exception.Message; }
    }

    public void SetCloseBehavior(WindowCloseBehavior mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, closeBehavior = mode.ToString() }));
            File.Move(temporary, path, overwrite: true);
            CloseBehavior = mode;
            Error = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Error = "未能记住窗口关闭方式：" + exception.Message; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }
}
