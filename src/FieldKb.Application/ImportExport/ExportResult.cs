namespace FieldKb.Application.ImportExport;

public sealed record ExportResult(
    string PackageId,
    string PackagePath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? BaseWatermarkUtc,
    DateTimeOffset MaxUpdatedAtUtc
);

