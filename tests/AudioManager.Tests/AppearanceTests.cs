using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using AudioManager.Desktop.Services;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Tests;

public sealed class AppearanceTests
{
    [Theory]
    [InlineData("switch")]
    [InlineData("persistence")]
    [InlineData("invalid")]
    [InlineData("controls")]
    public async Task AppearanceWorksWithoutChangingAudioConfiguration(string test)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(AppearanceTests).Assembly.Location);
        info.ArgumentList.Add("--appearance-fixture");
        info.ArgumentList.Add(test);
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
    }

    internal static int RunFixture(string test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "MixDeck-appearance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Application? app = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var root = app.Resources;
                root.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AudioManager.Desktop;component/Resources/Styles.xaml") });
                var path = Path.Combine(directory, "appearance.json");
                var audio = Path.Combine(directory, "settings.json");
                File.WriteAllText(audio, "audio configuration sentinel");
                var dark = false;
                if (test == "invalid") File.WriteAllText(path, "{\"version\":99,\"mode\":\"Dark\"}");
                using var themes = new ThemeService(root, Dispatcher.CurrentDispatcher, () => dark, path, listen: false);
                using var viewModel = new AppearanceViewModel(themes);
                Assert.Equal(AppThemeMode.System, themes.Mode);
                var surface = new Border();
                var window = new Window { Content = surface, Width = 200, Height = 200 };
                surface.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
                window.Measure(new(200, 200));
                window.Arrange(new(0, 0, 200, 200));
                var lightColor = ((SolidColorBrush)surface.Background).Color;
                Assert.Equal(Color.FromRgb(0xF5, 0xF7, 0xFB), lightColor);
                if (test == "controls")
                {
                    var creation = ConfigurationScenario.CreateAsync();
                    Pump(creation);
                    using var scenario = creation.GetAwaiter().GetResult();
                    scenario.Raw.AddSession("theme", .4f);
                    using var configuration = new ConfigurationViewModel(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario),
                        new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), new(), viewModel);
                    using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), new(), TimeProvider.System, configuration);
                    Pump(main.InitializeAsync());
                    var audioBefore = File.ReadAllText(scenario.Store.FilePath);
                    var mainWindow = new AudioManager.Desktop.Views.MainWindow(main);
                    var pages = (TabControl)mainWindow.FindName("Pages");
                    mainWindow.Measure(new(940, 740));
                    mainWindow.Arrange(new(0, 0, 940, 740));
                    mainWindow.UpdateLayout();
                    pages.SelectedIndex = 1;
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    mainWindow.UpdateLayout();
                    var advanced = (FrameworkElement)((TabItem)pages.Items[1]).Content;
                    advanced.Measure(new(820, 700));
                    advanced.Arrange(new(0, 0, 820, 700));
                    advanced.UpdateLayout();
                    var selector = Descendants(advanced).OfType<ComboBox>().Single(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "外观主题");
                    Assert.Equal(3, selector.Items.Count);
                    selector.SetCurrentValue(Selector.SelectedIndexProperty, 2);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    Assert.Equal(AppThemeMode.Dark, themes.Mode);
                    Assert.Equal(((SolidColorBrush)root["PrimaryTextBrush"]).Color, ((SolidColorBrush)selector.Foreground).Color);
                    Assert.Equal(((SolidColorBrush)root["WindowBackgroundBrush"]).Color, ((SolidColorBrush)mainWindow.Background).Color);
                    selector.SetCurrentValue(Selector.SelectedIndexProperty, 0);
                    Assert.Equal(AppThemeMode.System, themes.Mode);
                    dark = true;
                    var callback = typeof(ThemeService).GetMethod("OnSystemPreferenceChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    Task.Run(() => callback.Invoke(themes, new object?[] { null, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.General) })).GetAwaiter().GetResult();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(AppThemeMode.Dark, themes.EffectiveTheme);
                    Assert.Equal(audioBefore, File.ReadAllText(scenario.Store.FilePath));
                    Assert.Empty(scenario.Raw.Writes);
                    mainWindow.Close();
                }
                else if (test == "switch")
                {
                    var light = new ResourceDictionary { Source = new Uri("pack://application:,,,/AudioManager.Desktop;component/Resources/Themes/Light.xaml") };
                    var darkPalette = new ResourceDictionary { Source = new Uri("pack://application:,,,/AudioManager.Desktop;component/Resources/Themes/Dark.xaml") };
                    Assert.Equal(light.Keys.Cast<string>().Order(), darkPalette.Keys.Cast<string>().Order());
                    CheckTextContrast(light);
                    CheckTextContrast(darkPalette);
                    dark = true;
                    themes.RefreshSystemTheme();
                    Assert.Equal(AppThemeMode.Dark, themes.EffectiveTheme);
                    Assert.NotEqual(lightColor, ((SolidColorBrush)surface.Background).Color);
                    Assert.Contains("当前深色", viewModel.Description);
                    viewModel.Mode = AppThemeMode.Light;
                    themes.RefreshSystemTheme();
                    Assert.Equal(lightColor, ((SolidColorBrush)surface.Background).Color);
                    for (var i = 0; i < 30; i++) themes.SetMode(i % 2 == 0 ? AppThemeMode.Dark : AppThemeMode.Light);
                    Assert.Equal(2, root.MergedDictionaries.Count);
                    themes.SetMode(AppThemeMode.System);
                    var saved = File.ReadAllText(path);
                    dark = false;
                    themes.RefreshSystemTheme();
                    Assert.Equal(saved, File.ReadAllText(path));
                    Assert.Equal(AppThemeMode.System, themes.Mode);
                    themes.Dispose();
                    dark = true;
                    themes.RefreshSystemTheme();
                    Assert.Equal(AppThemeMode.Light, themes.EffectiveTheme);
                    Assert.Throws<ObjectDisposedException>(() => themes.SetMode(AppThemeMode.Dark));
                }
                else if (test == "persistence")
                {
                    viewModel.Mode = AppThemeMode.Dark;
                    var nextRoot = new ResourceDictionary();
                    using var reopened = new ThemeService(nextRoot, Dispatcher.CurrentDispatcher, () => false, path, listen: false);
                    Assert.Equal(AppThemeMode.Dark, reopened.Mode);
                    Assert.Equal(AppThemeMode.Dark, reopened.EffectiveTheme);
                    Assert.Equal("深色", viewModel.Options.Single(option => option.Mode == AppThemeMode.Dark).Label);
                    Assert.Null(viewModel.Error);
                }
                else
                {
                    Assert.NotNull(themes.Error);
                    Assert.Contains("99", File.ReadAllText(path));
                    using var invalidStore = new ThemeService(new ResourceDictionary(), Dispatcher.CurrentDispatcher, () => false,
                        Path.Combine(audio, "invalid.json"), listen: false);
                    invalidStore.SetMode(AppThemeMode.Dark);
                    Assert.Equal(AppThemeMode.Dark, invalidStore.EffectiveTheme);
                    Assert.NotNull(invalidStore.Error);
                    Assert.Throws<ArgumentOutOfRangeException>(() => themes.SetMode((AppThemeMode)99));
                }
                Assert.Equal("audio configuration sentinel", File.ReadAllText(audio));
            }
            catch (Exception exception) { failure = exception; }
            finally { app?.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown(); Directory.Delete(directory, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) return 1;
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void CheckTextContrast(ResourceDictionary palette)
    {
        foreach (var (foreground, background) in new[] { ("PrimaryTextBrush", "WindowBackgroundBrush"), ("SecondaryTextBrush", "WindowBackgroundBrush"),
            ("SecondaryTextBrush", "CardBackgroundBrush"), ("WarningBrush", "WarningBackgroundBrush"), ("ErrorBrush", "ErrorBackgroundBrush"),
            ("SuccessBrush", "SuccessBackgroundBrush"), ("OnAccentBrush", "AccentBrush") })
        {
            var a = Luminance(((SolidColorBrush)palette[foreground]).Color);
            var b = Luminance(((SolidColorBrush)palette[background]).Color);
            Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5, foreground + "/" + background);
        }
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
    }
}
