namespace FieldKb.Application.ImportExport;

public sealed record ExportRequest(
    string OutputDirectory,
    string RemoteInstanceId,
    ExportMode Mode,
    DateTimeOffset? UpdatedAfterUtc,
    int? Limit
);

