namespace FieldKb.Application.ImportExport;

public sealed record ImportReport(
    string PackageId,
    string ExporterInstanceId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    int ImportedCount,
    int SkippedCount,
    int ConflictCount,
    IReadOnlyList<string> Errors
)
{
    public int? ProblemsImportedCount { get; init; }
    public int? ProblemsSkippedCount { get; init; }
    public int? ProblemsConflictCount { get; init; }

    public int? TagsImportedCount { get; init; }
    public int? ProblemTagsImportedCount { get; init; }
    public int? AttachmentsImportedCount { get; init; }
}
