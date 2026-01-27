using FieldKb.Application.ImportExport;

namespace FieldKb.Application.Abstractions;

public interface IPackageTransferService
{
    Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken);

    Task<ImportReport> ImportAsync(string packagePath, CancellationToken cancellationToken);
}

