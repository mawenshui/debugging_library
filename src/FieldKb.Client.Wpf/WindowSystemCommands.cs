using System.Windows;
using System.Windows.Input;

namespace FieldKb.Client.Wpf;

public static class WindowSystemCommands
{
    public static ICommand Minimize { get; } = new SimpleCommand(p =>
    {
        if (p is Window w)
        {
            SystemCommands.MinimizeWindow(w);
        }
    });

    public static ICommand Maximize { get; } = new SimpleCommand(p =>
    {
        if (p is Window w)
        {
            SystemCommands.MaximizeWindow(w);
        }
    });

    public static ICommand Restore { get; } = new SimpleCommand(p =>
    {
        if (p is Window w)
        {
            SystemCommands.RestoreWindow(w);
        }
    });

    public static ICommand Close { get; } = new SimpleCommand(p =>
    {
        if (p is Window w)
        {
            SystemCommands.CloseWindow(w);
        }
    });

    public static ICommand ToggleMaximize { get; } = new SimpleCommand(p =>
    {
        if (p is not Window w)
        {
            return;
        }

        if (w.ResizeMode == ResizeMode.NoResize)
        {
            return;
        }

        if (w.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(w);
        }
        else
        {
            SystemCommands.MaximizeWindow(w);
        }
    });

    private sealed class SimpleCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public SimpleCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
