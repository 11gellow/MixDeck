using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioManager.Desktop.ViewModels;
using AudioManager.Desktop.Views;

namespace AudioManager.Tests;

public sealed class SliderInteractionTests
{
    [Theory]
    [InlineData("master")]
    [InlineData("session")]
    [InlineData("lifecycle")]
    public async Task TrackPressContinuesDraggingWithRealTemplateBindingsAndEditLifecycle(string scenario)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(typeof(SliderInteractionTests).Assembly.Location);
        info.ArgumentList.Add("--slider-track-fixture");
        info.ArgumentList.Add(scenario);
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
    }

    internal static int RunFixture(string name)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/AudioManager.Desktop;component/Resources/Styles.xaml", UriKind.Absolute)
                });
                RunWindow(name);
            }
            catch (Exception exception) { failure = exception; }
            finally { app?.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) { Console.Error.WriteLine("STA fixture timed out"); return 1; }
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine("真实 WPF 模板、绑定、捕获及模拟音频验证通过；未注入系统鼠标输入或写真实音量。");
        return 0;
    }

    private static void RunWindow(string name)
    {
        var creation = ConfigurationScenario.CreateAsync();
        Pump(creation);
        using var scenario = creation.GetAwaiter().GetResult();
        scenario.Raw.AddSession("slider", .4f);
        using var configuration = new ConfigurationViewModel(scenario.Service, new ConfigurationViewModelTests.Monitor(scenario),
            new ConfigurationViewModelTests.FakeStartup(), new ConfigurationViewModelTests.FakeDialogs(), new());
        using var main = new MainViewModel(scenario.Guard, new ConfigurationViewModelTests.Monitor(scenario), new(), new(), TimeProvider.System, configuration);
        var window = new MainWindow(main)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            Until(() => main.SyncState == SyncState.Live);
            window.UpdateLayout();
            var sliders = Descendants(window).OfType<Slider>().ToArray();
            var master = sliders.Single(slider => slider.DataContext is MainViewModel);
            var session = sliders.Single(slider => slider.DataContext is SessionViewModel);
            if (name == "lifecycle") CheckLifecycle(window, main, scenario, master);
            else
            {
                var slider = name == "master" ? master : session;
                var application = name == "session";
                var original = File.ReadAllText(scenario.Store.FilePath);
                PressTrack(slider, increase: true);
                Assert.Same(slider, Mouse.Captured);
                MoveTo(window, slider, .25);
                Assert.InRange(slider.Value, 23, 27);
                MoveTo(window, slider, .75);
                Assert.InRange(slider.Value, 73, 77);
                Assert.NotNull(slider.GetBindingExpression(Slider.ValueProperty));
                var actual = application ? scenario.Raw.Sessions.Values.Single().Volume.Level : scenario.Raw.Master.Level;
                Assert.InRange(actual, .73f, .77f);
                Assert.Equal(original, File.ReadAllText(scenario.Store.FilePath));
                MoveTo(window, slider, -1);
                Assert.Equal(0, slider.Value);
                MoveTo(window, slider, 2);
                Assert.Equal(100, slider.Value);
                Release(slider);
                Assert.NotSame(slider, Mouse.Captured);
                Until(() => Target(scenario, application) == 1);
                window.MoveTrackDrag(new Point(0, 0));
                Assert.Equal(100, slider.Value);
                PressTrack(slider, increase: false);
                MoveTo(window, slider, .35);
                var final = slider.Value;
                Release(slider);
                Until(() => Math.Abs(Target(scenario, application) * 100 - final) < .06,
                    () => $"expected={final}; saved={Target(scenario, application) * 100}; ui={slider.Value}; actual={scenario.Raw.Master}; captured={Mouse.Captured}; log={string.Join(" | ", main.Log.Entries.Select(item => item.Text))}");
                var source = PresentationSource.FromVisual(window)!;
                slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                slider.SetCurrentValue(Slider.ValueProperty, final + 1);
                slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
                    { RoutedEvent = Keyboard.PreviewKeyUpEvent });
                Until(() => Math.Abs(Target(scenario, application) * 100 - slider.Value) < .06);
                // Thumb 仍由 WPF 捕获，并通过原生 DragDelta 路由更新 Slider。
                var track = (Track)slider.Template.FindName("PART_Track", slider);
                track.Thumb!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                track.Thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.MouseDownEvent });
                Assert.True(track.Thumb.IsDragging);
                Assert.Same(track.Thumb, Mouse.Captured);
                var thumbStart = slider.Value;
                window.MoveTrackDrag(new Point(0, 0));
                Assert.Equal(thumbStart, slider.Value);
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(-15, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.True(slider.Value < thumbStart);
                Release(slider);
                Assert.False(track.Thumb.IsDragging);
                Assert.Null(Mouse.Captured);
                Until(() => Math.Abs(Target(scenario, application) * 100 - slider.Value) < .06);
                var beforeWheel = slider.Value;
                slider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Assert.Equal(beforeWheel + slider.SmallChange, slider.Value);
                Until(() => Math.Abs(Target(scenario, application) * 100 - slider.Value) < .06);
            }
            Pump(window.PrepareToLeaveAsync());
        }
        finally { Mouse.Capture(null); window.Close(); }
    }

    private static void CheckLifecycle(MainWindow window, MainViewModel main, ConfigurationScenario scenario, Slider slider)
    {
        foreach (var ending in new[] { "capture", "disable", "hide", "tab", "deactivate" })
        {
            PressTrack(slider, increase: true);
            Assert.Same(slider, Mouse.Captured);
            MoveTo(window, slider, .35);
            var value = slider.Value;
            switch (ending)
            {
                case "capture": Mouse.Capture(null); break;
                case "disable": slider.SetCurrentValue(UIElement.IsEnabledProperty, false); break;
                case "hide": window.Hide(); break;
                case "tab": ((TabControl)window.FindName("Pages")).SelectedIndex = 1; break;
                case "deactivate": typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]); break;
            }
            Assert.NotSame(slider, Mouse.Captured);
            Until(() => Math.Abs(Target(scenario, false) * 100 - value) < .06,
                () => $"ending={ending}; expected={value}; saved={Target(scenario, false) * 100}; ui={slider.Value}; actual={scenario.Raw.Master}; log={string.Join(" | ", main.Log.Entries.Select(item => item.Text))}");
            window.MoveTrackDrag(new Point(0, 0));
            Assert.Equal(value, slider.Value);
            slider.SetCurrentValue(UIElement.IsEnabledProperty, true);
            if (!window.IsVisible) window.Show();
            ((TabControl)window.FindName("Pages")).SelectedIndex = 0;
            window.UpdateLayout();
        }
        slider.SetCurrentValue(UIElement.IsEnabledProperty, false);
        var before = slider.Value;
        Assert.False(window.BeginTrackDrag(slider, new Point(0, 0)));
        Assert.Equal(before, slider.Value);
        slider.SetCurrentValue(UIElement.IsEnabledProperty, true);
        var saved = File.ReadAllText(scenario.Store.FilePath);
        PressTrack(slider, increase: true);
        MoveTo(window, slider, .25);
        Release(slider);
        // 前一次后台收尾尚未执行时开始下一次，不能提前结束新的编辑。
        PressTrack(slider, increase: false);
        MoveTo(window, slider, .75);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Same(slider, Mouse.Captured);
        Assert.Equal(saved, File.ReadAllText(scenario.Store.FilePath));
        Release(slider);
        Until(() => Math.Abs(Target(scenario, false) * 100 - slider.Value) < .06,
            () => $"rapid saved={Target(scenario, false) * 100}; ui={slider.Value}; actual={scenario.Raw.Master}; log={string.Join(" | ", main.Log.Entries.Select(item => item.Text))}");
        Pump(main.FlushEditsAsync());
    }

    private static void PressTrack(Slider slider, bool increase)
    {
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        var button = increase ? track.IncreaseRepeatButton! : track.DecreaseRepeatButton!;
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        button.RaiseEvent(args);
        Assert.True(args.Handled);
    }

    private static void Release(Slider slider) => slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseUpEvent });

    private static void MoveTo(MainWindow window, Slider slider, double ratio)
    {
        slider.UpdateLayout();
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        window.MoveTrackDrag(new Point(track.ActualWidth * ratio, track.ActualHeight / 2));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static float Target(ConfigurationScenario scenario, bool application) => scenario.Service.ProfileForDevice("device-a")!.Rules
        .Single(rule => (rule.Target.Target.Application is not null) == application).Target.Volume.Level;

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
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Until(Func<bool> condition, Func<string>? details = null)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        var timeout = Stopwatch.StartNew();
        timer.Tick += (_, _) => { if (condition() || timeout.Elapsed > TimeSpan.FromSeconds(8)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "WPF 绑定或编辑收尾未在期限内完成 " + details?.Invoke());
    }
}
