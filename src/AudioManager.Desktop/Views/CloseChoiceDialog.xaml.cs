using System.Windows;
using System.Windows.Input;
using AudioManager.Desktop.Services;

namespace AudioManager.Desktop.Views;

public partial class CloseChoiceDialog : Window
{
    public WindowCloseBehavior Choice { get; private set; } = WindowCloseBehavior.Ask;
    public bool Remember => RememberCheckBox.IsChecked == true;
    public CloseChoiceDialog(bool hasTray)
    {
        InitializeComponent();
        TrayButton.IsEnabled = hasTray;
        TrayUnavailableText.Visibility = hasTray ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnExit(object sender, RoutedEventArgs e) { Choice = WindowCloseBehavior.Exit; DialogResult = true; }
    private void OnTray(object sender, RoutedEventArgs e) { Choice = WindowCloseBehavior.Tray; DialogResult = true; }
    private void OnKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; } }
}
