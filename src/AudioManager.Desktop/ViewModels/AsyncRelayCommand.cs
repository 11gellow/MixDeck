using System.Windows.Input;

namespace AudioManager.Desktop.ViewModels;

/// <summary>执行期间禁止重入的异步命令；委托须自行处理业务异常。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;
    private bool isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        isRunning = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(); }
        finally
        {
            isRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
