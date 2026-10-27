using AudioManager.Desktop.Services;

namespace AudioManager.Desktop.ViewModels;

public sealed record ThemeOption(AppThemeMode Mode, string Label);

public sealed class AppearanceViewModel : ObservableObject, IDisposable
{
    private readonly ThemeService themes;
    public IReadOnlyList<ThemeOption> Options { get; } =
        [new(AppThemeMode.System, "跟随系统"), new(AppThemeMode.Light, "浅色"), new(AppThemeMode.Dark, "深色")];
    public AppThemeMode Mode { get => themes.Mode; set { if (value != themes.Mode) themes.SetMode(value); } }
    public string? Error => themes.Error;
    public string Description => themes.Mode == AppThemeMode.System ? $"跟随 Windows 应用主题 · 当前{(themes.EffectiveTheme == AppThemeMode.Dark ? "深色" : "浅色")}" : themes.Error is null ? "已记住外观偏好" : "主题已应用，偏好尚未保存";
    public AppearanceViewModel(ThemeService themes) { this.themes = themes; themes.Changed += OnChanged; }
    private void OnChanged() { OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(Description)); }
    public void Dispose() => themes.Changed -= OnChanged;
}
