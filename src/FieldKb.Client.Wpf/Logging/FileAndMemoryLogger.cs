using Microsoft.Extensions.Logging;

namespace FieldKb.Client.Wpf;

public sealed class FileAndMemoryLogger : ILogger
{
    private readonly string _category;
    private readonly IAppLogStore _store;
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minLevel;
    private readonly Func<IExternalScopeProvider?> _scopeProviderAccessor;

    public FileAndMemoryLogger(
        string category,
        IAppLogStore store,
        FileLogWriter writer,
        LogLevel minLevel,
        Func<IExternalScopeProvider?> scopeProviderAccessor)
    {
        _category = category;
        _store = store;
        _writer = writer;
        _minLevel = minLevel;
        _scopeProviderAccessor = scopeProviderAccessor;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _scopeProviderAccessor()?.Push(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _minLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var ts = DateTimeOffset.Now;

        var scopeText = string.Empty;
        var scopeProvider = _scopeProviderAccessor();
        if (scopeProvider is not null)
        {
            var parts = new List<string>();
            scopeProvider.ForEachScope((s, list) =>
            {
                list.Add(s?.ToString() ?? string.Empty);
            }, parts);

            if (parts.Count > 0)
            {
                scopeText = string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
        }

        var exceptionText = exception?.ToString();
        var line = $"{ts:yyyy-MM-dd HH:mm:ss.fff} [{ToChineseLevel(logLevel)}] {_category}({eventId.Id}) {message}";
        if (!string.IsNullOrWhiteSpace(scopeText))
        {
            line += $" | {scopeText}";
        }
        if (!string.IsNullOrWhiteSpace(exceptionText))
        {
            line += $"{Environment.NewLine}{exceptionText}";
        }

        _writer.Enqueue(line);

        _store.Append(new LogEntry(
            Timestamp: ts,
            Level: logLevel,
            Category: _category,
            EventId: eventId.Id,
            Message: string.IsNullOrWhiteSpace(scopeText) ? message : $"{message} | {scopeText}",
            Exception: exceptionText));
    }

    private static string ToChineseLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "跟踪",
            LogLevel.Debug => "调试",
            LogLevel.Information => "信息",
            LogLevel.Warning => "警告",
            LogLevel.Error => "错误",
            LogLevel.Critical => "严重",
            _ => level.ToString()
        };
    }
}

