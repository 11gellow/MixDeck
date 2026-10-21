using Microsoft.Win32;
using System.Windows;

namespace AudioManager.Desktop.Services;

public interface IConfigurationDialogs
{
    string? ChooseImport();
    string? ChooseExport();
    bool Confirm(string message);
}

public sealed class ConfigurationDialogs : IConfigurationDialogs
{
    public string? ChooseImport()
    {
        var dialog = new OpenFileDialog { Filter = "MixDeck 配置 (*.json)|*.json", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? ChooseExport()
    {
        var dialog = new SaveFileDialog { Filter = "MixDeck 配置 (*.json)|*.json", FileName = "MixDeck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public bool Confirm(string message) => MessageBox.Show(message, "配置管理", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
