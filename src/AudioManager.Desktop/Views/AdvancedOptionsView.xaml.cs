using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioManager.Desktop.ViewModels;

namespace AudioManager.Desktop.Views;

public partial class AdvancedOptionsView : UserControl
{
    public AdvancedOptionsView() => InitializeComponent();

    private async void OnVolumeLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: RestoreRuleViewModel row }) await row.CommitVolumeAsync();
    }

    private async void OnVolumeKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: RestoreRuleViewModel row } || e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Escape) row.CancelVolume();
        else await row.CommitVolumeAsync();
        FocusAnchor.Focus();
    }
}
