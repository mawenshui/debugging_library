using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldKb.Client.Wpf;

public sealed partial class LogViewModel : ObservableObject
{
    private readonly IAppLogStore _store;

    public LogViewModel(IAppLogStore store)
    {
        _store = store;
        Entries = store.Entries;
        LogFilePath = store.SessionLogFilePath;
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
    }

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    [ObservableProperty]
    private string _logFilePath = string.Empty;

    public IRelayCommand OpenLogFolderCommand { get; }

    private void OpenLogFolder()
    {
        var dir = Path.GetDirectoryName(_store.SessionLogFilePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
