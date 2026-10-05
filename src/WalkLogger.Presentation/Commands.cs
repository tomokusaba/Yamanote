using System.Windows.Input;

namespace WalkLogger.Presentation;

internal sealed class ActionCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class AsyncCommand(Func<Task> execute, Func<Exception, Task> reportError) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        try { await execute(); }
        catch (Exception ex) { await reportError(ex); }
    }
}
