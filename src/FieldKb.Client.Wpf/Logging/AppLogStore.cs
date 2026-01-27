using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace FieldKb.Client.Wpf;

public sealed class AppLogStore : IAppLogStore
{
    private readonly ObservableCollection<LogEntry> _entries;
    private readonly Dispatcher? _dispatcher;

    public AppLogStore(string sessionLogFilePath)
    {
        SessionLogFilePath = sessionLogFilePath;
        _entries = new ObservableCollection<LogEntry>();
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
    }

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    public string SessionLogFilePath { get; }

    public void Append(LogEntry entry)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            _entries.Add(entry);
            TrimIfNeeded();
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            _entries.Add(entry);
            TrimIfNeeded();
        }, DispatcherPriority.Background);
    }

    private void TrimIfNeeded()
    {
        const int max = 5000;
        while (_entries.Count > max)
        {
            _entries.RemoveAt(0);
        }
    }
}

