using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    public event EventHandler? ExitRequested;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = this.viewModel = viewModel;
        Loaded += OnLoaded;
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnSliderMouseDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnSliderMouseMove), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnSliderMouseUp), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnSliderCaptureLost), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => EndSlider()), true);
        IsVisibleChanged += async (_, _) => { if (!IsVisible) await PrepareToLeaveAsync(); };
        Deactivated += (_, _) => EndSlider();
    }

    internal void SetTrayAvailable(bool available)
        => LifetimeText.Text = available ? "关闭或最小化后在托盘继续运行；双击托盘显示，右键托盘退出。" : "托盘不可用；关闭窗口将退出程序。";

    private void OnExitClicked(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    public async Task PrepareToLeaveAsync()
    {
        EndSlider();
        Keyboard.ClearFocus();
        await viewModel.FlushEditsAsync();
    }

    private async void OnPageChanged(object sender, SelectionChangedEventArgs e)
    { if (viewModel is not null && ReferenceEquals(e.Source, Pages)) await PrepareToLeaveAsync(); }

    private bool sliderInput;
    private Slider? inputSlider;
    private Slider? trackDragSlider;
    private Track? trackDragTrack;
    private long sliderInputVersion;
    private void BeginSlider(object sender)
    {
        if (sender is not Slider { IsEnabled: true } slider) return;
        if (ReferenceEquals(inputSlider, slider) && sliderInput) return;
        EndSlider();
        sliderInput = true;
        sliderInputVersion++;
        inputSlider = slider;
        slider.IsEnabledChanged += OnActiveSliderAvailabilityChanged;
        slider.IsVisibleChanged += OnActiveSliderAvailabilityChanged;
        viewModel.BeginVolumeEdit(slider.DataContext as SessionViewModel);
    }
    private void OnSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.Handled) return;
        var source = e.OriginalSource as DependencyObject;
        var slider = FindVolumeSlider(source);
        if (slider is not { IsEnabled: true } || slider.Template.FindName("PART_Track", slider) is not Track track) return;
        if (track.Thumb is { } thumb && source is Visual visual && (ReferenceEquals(thumb, visual) || thumb.IsAncestorOf(visual)))
        {
            slider.Focus();
            BeginSlider(slider);
            return; // 球体保留原生拖动与鼠标捕获。
        }
        e.Handled = true; // 不再让 IsMoveToPointEnabled 或 RepeatButton 重复处理。
        BeginTrackDrag(slider, e.GetPosition(track));
    }

    private Slider? FindVolumeSlider(DependencyObject? source)
    {
        for (var current = source; current is Visual; current = VisualTreeHelper.GetParent(current))
            if (current is Slider slider && ReferenceEquals(slider.Style, TryFindResource("VolumeSliderStyle"))) return slider;
        return null;
    }

    internal bool BeginTrackDrag(Slider slider, Point point)
    {
        if (!slider.IsEnabled || slider.Template.FindName("PART_Track", slider) is not Track track) return false;
        slider.Focus(); // 先完成旧焦点的收尾，再开始新的编辑。
        BeginSlider(slider);
        if (!slider.CaptureMouse())
        {
            SetTrackValue(slider, track, point);
            EndSlider();
            return false;
        }
        trackDragSlider = slider;
        trackDragTrack = track;
        MoveTrackDrag(point);
        return true;
    }

    private void OnSliderMouseMove(object sender, MouseEventArgs e)
    {
        if (trackDragTrack is not { } track) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndSlider(); return; }
        MoveTrackDrag(e.GetPosition(track));
        e.Handled = true;
    }

    internal void MoveTrackDrag(Point point)
    {
        if (trackDragSlider is { IsEnabled: true } slider && trackDragTrack is { } track)
            SetTrackValue(slider, track, point);
    }

    private static void SetTrackValue(Slider slider, Track track, Point point)
    {
        var value = track.ValueFromPoint(point);
        if (double.IsFinite(value)) slider.SetCurrentValue(Slider.ValueProperty, Math.Clamp(value, slider.Minimum, slider.Maximum));
    }

    private void OnActiveSliderAvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (inputSlider is { } slider && (!slider.IsEnabled || !slider.IsVisible)) EndSlider();
    }
    private void OnSliderKeyDown(object sender, KeyEventArgs e)
    { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) BeginSlider(sender); }
    private void OnSliderKeyUp(object sender, KeyEventArgs e)
    { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) EndSlider(); }
    private void OnSliderWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled) return;
        BeginSlider(sender);
        slider.SetCurrentValue(Slider.ValueProperty, Math.Clamp(slider.Value + Math.Sign(e.Delta) * slider.SmallChange, slider.Minimum, slider.Maximum));
        e.Handled = true;
        EndSlider();
    }
    private void OnSliderMouseUp(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left) EndSlider(); }
    private void OnSliderCaptureLost(object sender, MouseEventArgs e)
    {
        if (inputSlider is { } slider && !slider.IsMouseCaptureWithin) EndSlider();
    }
    private void EndSlider()
    {
        if (!sliderInput) return;
        sliderInput = false;
        var slider = inputSlider;
        inputSlider = null;
        trackDragSlider = null;
        trackDragTrack = null;
        if (slider is not null)
        {
            slider.IsEnabledChanged -= OnActiveSliderAvailabilityChanged;
            slider.IsVisibleChanged -= OnActiveSliderAvailabilityChanged;
            if (ReferenceEquals(Mouse.Captured, slider)) slider.ReleaseMouseCapture();
            else if (Mouse.Captured is Thumb thumb && slider.IsAncestorOf(thumb)) thumb.CancelDrag();
        }
        var version = sliderInputVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            if (!sliderInput && version == sliderInputVersion) await viewModel.FinishVolumeEditsAsync();
        }));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await viewModel.InitializeAsync();
    }
}
