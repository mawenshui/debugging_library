using Microsoft.Extensions.Logging;

namespace FieldKb.Client.Wpf;

public sealed class FileAndMemoryLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly IAppLogStore _store;
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minLevel;
    private IExternalScopeProvider? _scopeProvider;

    public FileAndMemoryLoggerProvider(IAppLogStore store, FileLogWriter writer, LogLevel minLevel)
    {
        _store = store;
        _writer = writer;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileAndMemoryLogger(categoryName, _store, _writer, _minLevel, () => _scopeProvider);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}

