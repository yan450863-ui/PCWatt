using System.Windows.Input;

namespace PCWatt.App.ViewModels;

public sealed class AsyncRelayCommand(Func<object?, Task> execute, Action<Exception>? onError = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter); }
        catch (Exception exception) { onError?.Invoke(exception); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
