namespace FieldKb.Infrastructure.SpreadsheetImport;

public interface ISpreadsheetImportService
{
    Task<SpreadsheetImportReport> PreviewAsync(SpreadsheetImportRequest request, CancellationToken cancellationToken);

    Task<SpreadsheetImportReport> ImportAsync(SpreadsheetImportRequest request, CancellationToken cancellationToken);
}
