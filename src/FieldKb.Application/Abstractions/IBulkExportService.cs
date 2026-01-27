namespace FieldKb.Application.Abstractions;

public interface IBulkExportService
{
    Task BulkExportAsync(BulkExportRequest request, CancellationToken cancellationToken);
}

public enum BulkExportFormat
{
    Csv = 0,
    Jsonl = 1,
    Xlsx = 2,
    XlsxBundle = 3
}

public sealed record BulkExportRequest(
    string OutputPath,
    BulkExportFormat Format,
    ProblemHardDeleteFilter Filter);
