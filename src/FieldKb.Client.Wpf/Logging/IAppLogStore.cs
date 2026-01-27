using System.Collections.ObjectModel;

namespace FieldKb.Client.Wpf;

public interface IAppLogStore
{
    ReadOnlyObservableCollection<LogEntry> Entries { get; }

    string SessionLogFilePath { get; }

    void Append(LogEntry entry);
}

