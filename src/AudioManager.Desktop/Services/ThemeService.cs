using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AudioManager.Desktop.Services;

public enum AppThemeMode { System, Light, Dark }

/// <summary>仅管理外观资源与独立外观偏好，不读取或修改音量配置。</summary>
public sealed class ThemeService : IDisposable
{
    private readonly ResourceDictionary resources;
    private readonly Dispatcher dispatcher;
    private readonly Func<bool> systemIsDark;
    private readonly string filePath;
    private readonly bool listen;
    private ResourceDictionary? palette;
    private bool disposed;
    private string? systemListenerError;
    public AppThemeMode Mode { get; private set; } = AppThemeMode.System;
    public AppThemeMode EffectiveTheme { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public ThemeService(ResourceDictionary resources, Dispatcher dispatcher, Func<bool> systemIsDark, string filePath, bool listen = true)
    {
        this.resources = resources;
        this.dispatcher = dispatcher;
        this.systemIsDark = systemIsDark;
        this.filePath = filePath;
        try
        {
            if (File.Exists(filePath))
            {
                if (new FileInfo(filePath).Length > 16384) throw new InvalidDataException("外观偏好文件过大，暂时跟随系统。");
                using var document = JsonDocument.Parse(File.ReadAllText(filePath));
                var root = document.RootElement;
                var text = root.GetProperty("mode").GetString();
                if (root.GetProperty("version").GetInt32() != 1 || !Enum.TryParse<AppThemeMode>(text, out var mode) || !Enum.IsDefined(mode) || text != mode.ToString())
                    throw new InvalidDataException("不支持的外观偏好，暂时跟随系统。");
                Mode = mode;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { Error = "外观偏好读取失败，暂时跟随系统：" + exception.Message; }
        Apply();
        if (listen)
        {
            try { SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged; this.listen = true; }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            { Error = systemListenerError = "系统主题监听不可用，仍可手动选择浅色或深色：" + exception.Message; }
        }
    }

    public void SetMode(AppThemeMode mode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        dispatcher.VerifyAccess();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
        Apply();
        var temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, mode = mode.ToString() }));
            File.Move(temporary, filePath, overwrite: true);
            Error = mode == AppThemeMode.System ? systemListenerError : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Error = "主题已切换，但外观偏好保存失败：" + exception.Message; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    public void RefreshSystemTheme()
    {
        if (disposed || Mode != AppThemeMode.System) return;
        dispatcher.VerifyAccess();
        Apply();
        Changed?.Invoke();
    }

    private void Apply()
    {
        var effective = Mode == AppThemeMode.System ? (systemIsDark() ? AppThemeMode.Dark : AppThemeMode.Light) : Mode;
        if (palette is not null && effective == EffectiveTheme) return;
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/AudioManager.Desktop;component/Resources/Themes/{effective}.xaml") };
        if (palette is null) resources.MergedDictionaries.Add(next);
        else resources.MergedDictionaries[resources.MergedDictionaries.IndexOf(palette)] = next;
        palette = next;
        EffectiveTheme = effective;
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (!disposed && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(() => { if (!disposed) RefreshSystemTheme(); });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (listen) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        Changed = null;
    }
}
