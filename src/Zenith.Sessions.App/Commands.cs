using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Zenith.Sessions.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
}
public sealed class Command(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed class AsyncCommand(Func<object?, Task> execute, Action<Exception> failed,
    Func<object?, object?>? captureParameter = null) : ICommand
{
    private bool _busy;
    public bool CanExecute(object? parameter) => !_busy;
    public Task Execution { get; private set; } = Task.CompletedTask;
    public void Execute(object? parameter)
    { if (!_busy) Execution = RunAsync(captureParameter is null ? parameter : captureParameter(parameter)); }
    private async Task RunAsync(object? parameter)
    {
        if (_busy) return; _busy = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failed(ex); }
        finally { _busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    public event EventHandler? CanExecuteChanged;
}
