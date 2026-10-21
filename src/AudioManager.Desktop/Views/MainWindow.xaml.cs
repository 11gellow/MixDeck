using System.Windows;
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
    }

    internal void SetTrayAvailable(bool available)
        => LifetimeText.Text = available ? "关闭或最小化后在托盘继续运行；双击托盘显示，右键托盘退出。" : "托盘不可用；关闭窗口将退出程序。";

    private void OnExitClicked(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnConfigurationClicked(object sender, RoutedEventArgs e)
    {
        if (viewModel.Configuration is { } configuration)
            new ConfigurationWindow(configuration) { Owner = this }.ShowDialog();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await viewModel.InitializeAsync();
    }
}
