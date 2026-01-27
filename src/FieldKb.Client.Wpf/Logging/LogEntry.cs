using Microsoft.Extensions.Logging;

namespace FieldKb.Client.Wpf;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    int EventId,
    string Message,
    string? Exception);

