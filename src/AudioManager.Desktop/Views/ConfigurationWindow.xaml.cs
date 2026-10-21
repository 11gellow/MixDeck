using System.Windows;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Desktop.Views;

public partial class ConfigurationWindow : Window
{
    public ConfigurationWindow(ConfigurationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
