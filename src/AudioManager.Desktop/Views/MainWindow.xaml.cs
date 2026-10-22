using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnSliderMouseUp), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnSliderCaptureLost), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => EndSlider()), true);
        IsVisibleChanged += async (_, _) => { if (!IsVisible) await PrepareToLeaveAsync(); };
    }

    internal void SetTrayAvailable(bool available)
        => LifetimeText.Text = available ? "关闭或最小化后在托盘继续运行；双击托盘显示，右键托盘退出。" : "托盘不可用；关闭窗口将退出程序。";

    private void OnExitClicked(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    public async Task PrepareToLeaveAsync()
    {
        Keyboard.ClearFocus();
        await viewModel.FlushEditsAsync();
    }

    private async void OnPageChanged(object sender, SelectionChangedEventArgs e)
    { if (viewModel is not null && ReferenceEquals(e.Source, Pages)) await PrepareToLeaveAsync(); }

    private bool sliderInput;
    private void BeginSlider(object sender)
    {
        if (sender is not Slider slider) return;
        sliderInput = true;
        viewModel.BeginVolumeEdit(slider.DataContext as SessionViewModel);
    }
    private void OnSliderMouseDown(object sender, MouseButtonEventArgs e) => BeginSlider(sender);
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
    private void OnSliderMouseUp(object sender, MouseButtonEventArgs e) => EndSlider();
    private void OnSliderCaptureLost(object sender, MouseEventArgs e) => EndSlider();
    private void EndSlider()
    {
        if (!sliderInput) return;
        sliderInput = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await viewModel.FinishVolumeEditsAsync()));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await viewModel.InitializeAsync();
    }
}
