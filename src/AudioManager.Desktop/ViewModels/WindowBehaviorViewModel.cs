using AudioManager.Desktop.Services;

namespace AudioManager.Desktop.ViewModels;

public sealed record CloseBehaviorOption(WindowCloseBehavior Mode, string Label);

public sealed class WindowBehaviorViewModel : ObservableObject, IDisposable
{
    private readonly WindowPreferencesStore preferences;
    public IReadOnlyList<CloseBehaviorOption> Options { get; } =
        [new(WindowCloseBehavior.Ask, "每次询问"), new(WindowCloseBehavior.Exit, "退出程序"), new(WindowCloseBehavior.Tray, "最小化到托盘")];
    public WindowCloseBehavior Mode
    {
        get => preferences.CloseBehavior;
        set { if (value != Mode) preferences.SetCloseBehavior(value); }
    }
    public string? Error => preferences.Error;
    public WindowBehaviorViewModel(WindowPreferencesStore preferences)
    { this.preferences = preferences; preferences.Changed += OnChanged; }
    private void OnChanged() { OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(Error)); }
    public void Dispose() => preferences.Changed -= OnChanged;
}
