using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioManager.Desktop.Services;
using AudioManager.Desktop.ViewModels;
using AudioManager.Desktop.Views;

namespace AudioManager.Tests;

public sealed class FrontendRefreshTests
{
    [Theory]
    [InlineData("navigation")]
    [InlineData("closing")]
    [InlineData("motion")]
    [InlineData("realtime")]
    [InlineData("startup")]
    public async Task RealWpfInteractionUsesFakeAudioAndIsolatedPreferences(string scenario)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(FrontendRefreshTests).Assembly.Location);
        info.ArgumentList.Add("--frontend-refresh-fixture"); info.ArgumentList.Add(scenario);
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
    }

    [Fact]
    public void ClosePreferencesPersistIndependentlyAndInvalidOrFailedWritesKeepSafeDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MixDeck-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "window.json");
            var audio = Path.Combine(directory, "settings.json"); File.WriteAllText(audio, "unchanged");
            var store = new WindowPreferencesStore(path);
            Assert.Equal(WindowCloseBehavior.Ask, store.CloseBehavior);
            store.SetCloseBehavior(WindowCloseBehavior.Tray);
            Assert.Equal(WindowCloseBehavior.Tray, new WindowPreferencesStore(path).CloseBehavior);
            using var viewModel = new WindowBehaviorViewModel(store);
            viewModel.Mode = WindowCloseBehavior.Exit;
            Assert.Equal(WindowCloseBehavior.Exit, new WindowPreferencesStore(path).CloseBehavior);
            viewModel.Mode = WindowCloseBehavior.Ask;
            Assert.Equal(WindowCloseBehavior.Ask, new WindowPreferencesStore(path).CloseBehavior);
            File.WriteAllText(path, "{\"version\":99,\"closeBehavior\":\"Exit\"}");
            var invalid = new WindowPreferencesStore(path);
            Assert.Equal(WindowCloseBehavior.Ask, invalid.CloseBehavior);
            Assert.NotNull(invalid.Error); Assert.Contains("99", File.ReadAllText(path));
            var blocked = new WindowPreferencesStore(Path.Combine(audio, "window.json"));
            blocked.SetCloseBehavior(WindowCloseBehavior.Exit);
            Assert.NotNull(blocked.Error);
            Assert.Equal(WindowCloseBehavior.Ask, blocked.CloseBehavior);
            Assert.Equal("unchanged", File.ReadAllText(audio));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.SetCloseBehavior((WindowCloseBehavior)99));
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static int RunFixture(string scenario)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AudioManager.Desktop;component/Resources/Styles.xaml") });
                if (scenario == "startup") CheckStartupWithoutWindow();
                else if (scenario == "navigation") CheckNavigation();
                else if (scenario == "motion") CheckMotion();
                else if (scenario == "realtime") CheckRealtimeMotion();
                else CheckClosing();
            }
            catch (Exception exception) { failure = exception; }
            finally { Mouse.Capture(null); app?.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) return 1;
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private sealed class CountedStartup : AudioManager.Core.Interfaces.ILoginStartup
    {
        public int Reads;
        public bool IsEnabled { get { Interlocked.Increment(ref Reads); return true; } }
        public void SetEnabled(bool enabled) => throw new InvalidOperationException("Read only fixture");
    }

    private static void CheckStartupWithoutWindow()
    {
        var creation = ConfigurationScenario.CreateAsync(); Pump(creation);
        using var scenario = creation.GetAwaiter().GetResult();
        var startup = new CountedStartup();
        var target = scenario.Raw.Master;
        var save = scenario.Service.SaveSnapshotAsync(scenario.State(), "startup target"); Pump(save); save.GetAwaiter().GetResult();
        scenario.Raw.Master = new(.07f, !target.IsMuted);
        var monitor = new ConfigurationViewModelTests.Monitor(scenario);
        using var configuration = new ConfigurationViewModel(scenario.Service, monitor, startup, new ConfigurationViewModelTests.FakeDialogs(), new());
        using var main = new MainViewModel(scenario.Guard, monitor, new(), new(), TimeProvider.System, configuration);
        main.DeferPresentation();
        var coordinator = new DesktopStartup(main.InitializeAsync, () =>
        {
            Assert.Empty(Application.Current.Windows.Cast<Window>());
            Assert.True(main.HasMasterVolume);
            Assert.Equal(target, scenario.Raw.Master);
            Assert.NotNull(scenario.Service.LoadResult);
            Assert.Equal(0, startup.Reads);
            return true;
        }, () => throw new InvalidOperationException("Tray startup must not construct a window"));
        var task = coordinator.StartAsync(true, default); Pump(task); task.GetAwaiter().GetResult();
        Assert.False(configuration.IsStartupStateKnown);
        var probe = configuration.RefreshStartupStateAsync(); Pump(probe); probe.GetAwaiter().GetResult();
        Assert.True(configuration.StartAtLogin); Assert.True(configuration.IsStartupStateKnown);
        var window = new MainWindow(main) { Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false };
        try { main.ActivatePresentation(); window.Show(); window.UpdateLayout(); Assert.True(main.HasMasterVolume); }
        finally { window.Close(); }
    }

    private static void CheckNavigation()
    {
        var creation = ConfigurationScenario.CreateAsync(); Pump(creation);
        using var scenario = creation.GetAwaiter().GetResult();
        var longPath = @"C:\Program Files\WindowsApps\" + new string('x', 180) + @"\Player.exe";
        scenario.Raw.AddSession("long", .4f, false, longPath);
        var log = new OperationLog();
        using var configuration = new ConfigurationViewModel(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario),
            new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), log);
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), log, TimeProvider.System, configuration);
        Pump(main.InitializeAsync());
        var audioBefore = File.ReadAllText(scenario.Store.FilePath); var writes = scenario.Raw.Writes.Count;
        var window = new MainWindow(main) { Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var pages = (SlidingTabControl)window.FindName("Pages");
            Assert.Equal(new[] { "音量管理", "高级选项", "系统日志" }, pages.Items.Cast<TabItem>().Select(tab => tab.Header));
            var track = (FrameworkElement)pages.Template.FindName("PART_NavigationTrack", pages);
            Assert.True(pages.BeginNavigationDrag(10));
            pages.MoveNavigationDrag(track.ActualWidth * .5);
            Assert.Equal(1, pages.SelectedIndex);
            pages.MoveNavigationDrag(track.ActualWidth + 100);
            Assert.Equal(2, pages.SelectedIndex);
            pages.EndNavigationDrag(); Assert.NotSame(track, Mouse.Captured);
            Assert.True(pages.BeginNavigationDrag(track.ActualWidth * .5));
            Mouse.Capture(null); pages.MoveNavigationDrag(0); Assert.Equal(2, pages.SelectedIndex);
            Assert.True(pages.BeginNavigationDrag(track.ActualWidth * .5));
            pages.IsEnabled = false; Assert.NotSame(track, Mouse.Captured);
            Assert.False(pages.BeginNavigationDrag(0)); pages.IsEnabled = true;
            Assert.True(pages.BeginNavigationDrag(track.ActualWidth * .5)); pages.EndNavigationDrag();
            Assert.Equal(1, pages.SelectedIndex);
            window.UpdateLayout();
            var advanced = (AdvancedOptionsView)((TabItem)pages.Items[1]).Content;
            var memory = (Expander)advanced.FindName("MemorySettings");
            Assert.False(memory.IsExpanded);
            memory.IsExpanded = true; window.UpdateLayout();
            var grid = (DataGrid)advanced.FindName("RememberedVolumes");
            Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(grid));
            Assert.True(grid.Columns.Sum(column => column.ActualWidth) > grid.ActualWidth);
            var scroll = Descendants(grid).OfType<ScrollViewer>().First();
            Assert.True(scroll.ScrollableWidth > 0);
            scroll.ScrollToRightEnd(); window.UpdateLayout(); Assert.True(scroll.HorizontalOffset > 0);
            Assert.All(Descendants(grid).OfType<DataGridCell>(), cell => Assert.True(cell.ClipToBounds));
            Assert.DoesNotContain(Descendants(advanced).OfType<ComboBox>(), combo => AutomationProperties.GetName(combo) == "已保存方案");
            memory.IsExpanded = false;
            log.Info("测试日志实时更新", "本软件");
            pages.SelectedIndex = 2; window.UpdateLayout();
            var logView = (SystemLogView)((TabItem)pages.Items[2]).Content;
            Assert.Same(log, logView.DataContext);
            Assert.Contains(Descendants(logView).OfType<TextBlock>(), text => text.Text == "测试日志实时更新");
            Assert.Equal(audioBefore, File.ReadAllText(scenario.Store.FilePath)); Assert.Equal(writes, scenario.Raw.Writes.Count);
        }
        finally { window.Close(); }
    }

    private static void CheckRealtimeMotion()
    {
        var creation = ConfigurationScenario.CreateAsync(); Pump(creation);
        using var scenario = creation.GetAwaiter().GetResult();
        var log = new OperationLog();
        using var configuration = new ConfigurationViewModel(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario),
            new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), log);
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), log, TimeProvider.System, configuration);
        Pump(main.InitializeAsync());
        var window = new MainWindow(main) { Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout(); Pump(Task.Delay(250));
            var pages = (SlidingTabControl)window.FindName("Pages");
            var track = (FrameworkElement)pages.Template.FindName("PART_NavigationTrack", pages);
            var indicator = (FrameworkElement)pages.Template.FindName("PART_NavigationIndicator", pages);
            var transform = (TranslateTransform)indicator.RenderTransform;
            var width = track.ActualWidth / 3;
            void Click(int index)
            {
                Assert.True(pages.BeginNavigationDrag(width * (index + .5))); pages.EndNavigationDrag();
                Assert.Equal(index, pages.SelectedIndex);
            }
            void VerifyNatural(string name, Action trigger, double target)
            {
                var from = transform.X;
                var samples = new List<(double Milliseconds, double X)>();
                trigger();
                if (SystemParameters.ClientAreaAnimation) Assert.InRange(transform.X, from - .01, from + .01);
                var stopwatch = Stopwatch.StartNew(); samples.Add((0, transform.X));
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    samples.Add((stopwatch.Elapsed.TotalMilliseconds, transform.X));
                    if (stopwatch.ElapsedMilliseconds >= 400) { timer.Stop(); frame.Continue = false; }
                };
                timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
                Console.WriteLine($"{name}; ClientAreaAnimation={SystemParameters.ClientAreaAnimation}; "
                    + string.Join("; ", samples.Select(sample => $"{sample.Milliseconds:F0}ms={sample.X:F2}")));
                Assert.InRange(samples[^1].X, target - .01, target + .01);
                if (!SystemParameters.ClientAreaAnimation) return;
                var minimum = Math.Min(from, target); var maximum = Math.Max(from, target);
                Assert.Contains(samples, sample => sample.Milliseconds > 0 && sample.X > minimum + .1 && sample.X < maximum - .1);
                Assert.All(samples, sample => Assert.InRange(sample.X, minimum - .01, maximum + .01));
            }
            VerifyNatural("click-adjacent", () => Click(1), width);
            VerifyNatural("click-across-two", () => Click(0), 0);
            VerifyNatural("click-across-two", () => Click(2), width * 2);
            VerifyNatural("click-return", () => Click(0), 0);
            Click(2); Pump(Task.Delay(70));
            VerifyNatural("reverse-mid-animation", () => Click(0), 0);
            VerifyNatural("drag-detent", () =>
            {
                Assert.True(pages.BeginNavigationDrag(.5 * width));
                pages.MoveNavigationDrag(width + 5); pages.EndNavigationDrag();
            }, width);
        }
        finally { window.Close(); }
    }

    private static void CheckMotion()
    {
        foreach (var palette in new[] { "Light", "Dark" })
        {
            var resources = new ResourceDictionary { Source = new Uri($"pack://application:,,,/AudioManager.Desktop;component/Resources/Themes/{palette}.xaml") };
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var pages = new SlidingTabControl { AnimationsEnabledOverride = true };
            pages.SetResourceReference(FrameworkElement.StyleProperty, "MainNavigationStyle");
            foreach (var title in new[] { "音量管理", "高级选项", "系统日志" }) pages.Items.Add(new TabItem { Header = title, Content = new TextBlock { Text = "正文颜色" } });
            var window = new Window { Width = 640, Height = 250, Left = -30000, Top = -30000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Content = pages };
            window.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            try
            {
                window.Show(); window.UpdateLayout();
                var track = (FrameworkElement)pages.Template.FindName("PART_NavigationTrack", pages);
                var indicator = (FrameworkElement)pages.Template.FindName("PART_NavigationIndicator", pages);
                var transform = (TranslateTransform)indicator.RenderTransform;
                var width = track.ActualWidth / 3;
                void Near(double value) => Assert.InRange(transform.X, value - .01, value + .01);
                void Tick(int ms)
                {
                    // 等待自然 Begin 被时钟消费后再做确定性定位；不可在生产路径 Seek 来迎合取帧。
                    var clock = (System.Windows.Media.Animation.AnimationClock?)typeof(SlidingTabControl)
                        .GetField("movement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pages);
                    var timeout = Stopwatch.StartNew();
                    while (clock is { CurrentState: System.Windows.Media.Animation.ClockState.Stopped } && timeout.ElapsedMilliseconds < 1000)
                        Pump(Task.Delay(10));
                    pages.SeekAnimations(TimeSpan.FromMilliseconds(ms));
                }
                void Click(int index)
                {
                    var previous = pages.SelectedIndex;
                    Assert.True(pages.BeginNavigationDrag((index + .5) * width));
                    Assert.Equal(previous, pages.SelectedIndex); // 按下不能先换页/跳动滑块。
                    pages.EndNavigationDrag();
                    Assert.Equal(index, pages.SelectedIndex);
                }
                Near(0);
                Click(1); Near(0);
                Tick(100); Assert.InRange(transform.X, .01, width - .01);
                Tick(200); Near(width);
                Click(2); Near(width);
                Tick(100); var interrupted = transform.X;
                Assert.InRange(interrupted, width + .01, 2 * width - .01);
                Click(0); Near(interrupted); // 新动画必须从正在显示的位置开始。
                Tick(100); Assert.InRange(transform.X, .01, interrupted - .01);
                Tick(200); Near(0);
                Click(2); Tick(100); Assert.InRange(transform.X, .01, 2 * width - .01);
                Tick(200); Near(2 * width);
                Click(2); Near(2 * width); // 当前项不发生抖动。

                Assert.True(pages.BeginNavigationDrag(2.5 * width));
                pages.MoveNavigationDrag(2 * width - 1); Assert.Equal(2, pages.SelectedIndex);
                pages.MoveNavigationDrag(2 * width - 5); Assert.Equal(1, pages.SelectedIndex);
                pages.MoveNavigationDrag(2 * width + 2); Assert.Equal(1, pages.SelectedIndex);
                pages.MoveNavigationDrag(2 * width + 5); Assert.Equal(2, pages.SelectedIndex);
                pages.MoveNavigationDrag(-100); Assert.Equal(0, pages.SelectedIndex);
                pages.EndNavigationDrag(); Tick(200); Near(0);

                Assert.True(pages.BeginNavigationDrag(width * 1.5));
                Mouse.Capture(null); Near(0); Assert.Equal(0, pages.SelectedIndex);
                Assert.True(pages.BeginNavigationDrag(width * 1.5));
                pages.MoveNavigationDrag(width * 1.5 + 10); Assert.Equal(1, pages.SelectedIndex);
                Mouse.Capture(null); Near(width);
                Assert.True(pages.BeginNavigationDrag(width * 2.5));
                pages.IsEnabled = false; Assert.NotSame(track, Mouse.Captured); Near(width);
                pages.IsEnabled = true;
                Assert.True(pages.BeginNavigationDrag(width * 2.5));
                typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, [EventArgs.Empty]);
                Assert.NotSame(track, Mouse.Captured); Assert.Equal(1, pages.SelectedIndex); Near(width);

                var source = PresentationSource.FromVisual(window)!;
                foreach (var (key, index) in new[] { (Key.Home, 0), (Key.End, 2) })
                {
                    pages.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
                    Assert.Equal(index, pages.SelectedIndex); Tick(200); Near(index * width);
                }
                window.Width = 760; window.UpdateLayout();
                Assert.Equal(track.ActualWidth / 3, indicator.Width); Near(2 * track.ActualWidth / 3);
                pages.AnimationsEnabledOverride = false; Click(1); Near(width);
                var selected = (TabItem)pages.Items[1];
                var label = (ContentPresenter)selected.Template.FindName("NavigationLabel", selected);
                Assert.Equal(((SolidColorBrush)pages.FindResource("OnAccentBrush")).Color,
                    ((SolidColorBrush)System.Windows.Documents.TextElement.GetForeground(label)).Color);
                Assert.Equal(((SolidColorBrush)pages.FindResource("PrimaryTextBrush")).Color,
                    ((SolidColorBrush)((TextBlock)selected.Content).Foreground).Color);
                // 资源变更需更新控件管理的文字画刷，不能保留旧主题的颜色。
                var other = new ResourceDictionary { Source = new Uri($"pack://application:,,,/AudioManager.Desktop;component/Resources/Themes/{(palette == "Light" ? "Dark" : "Light")}.xaml") };
                Application.Current.Resources.MergedDictionaries.Add(other);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.Equal(((SolidColorBrush)pages.FindResource("OnAccentBrush")).Color,
                    ((SolidColorBrush)System.Windows.Documents.TextElement.GetForeground(label)).Color);
                Application.Current.Resources.MergedDictionaries.Remove(other);
            }
            finally { window.Close(); Application.Current.Resources.MergedDictionaries.Remove(resources); }
        }
    }

    private static void CheckClosing()
    {
        foreach (var mode in new[] { WindowCloseBehavior.Exit, WindowCloseBehavior.Tray })
        {
            var dialog = new CloseChoiceDialog(true) { Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
            dialog.Dispatcher.BeginInvoke(() =>
            {
                ((CheckBox)dialog.FindName("RememberCheckBox")).IsChecked = true;
                Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, mode == WindowCloseBehavior.Exit ? "退出程序" : "最小化到托盘"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            Assert.True(dialog.ShowDialog()); Assert.Equal(mode, dialog.Choice); Assert.True(dialog.Remember);
        }
        var cancelled = new CloseChoiceDialog(true) { Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        cancelled.Dispatcher.BeginInvoke(cancelled.Close, DispatcherPriority.ApplicationIdle);
        Assert.False(cancelled.ShowDialog()); Assert.Equal(WindowCloseBehavior.Ask, cancelled.Choice);
        var unavailable = new CloseChoiceDialog(false);
        Assert.False(((Button)unavailable.FindName("TrayButton")).IsEnabled);
        Assert.Equal(Visibility.Visible, ((TextBlock)unavailable.FindName("TrayUnavailableText")).Visibility);
        unavailable.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
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
}
