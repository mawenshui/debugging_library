namespace FieldKb.Infrastructure.SpreadsheetImport;

public enum SpreadsheetImportConflictPolicy
{
    SkipIfLocalNewer = 0,
    Overwrite = 1
}

public enum SpreadsheetImportTagMergeMode
{
    Replace = 0,
    Merge = 1
}

public sealed record SpreadsheetImportRequest(
    string InputPath,
    SpreadsheetImportConflictPolicy ConflictPolicy = SpreadsheetImportConflictPolicy.SkipIfLocalNewer,
    SpreadsheetImportTagMergeMode TagMergeMode = SpreadsheetImportTagMergeMode.Replace);

public sealed record SpreadsheetImportReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    int ProblemsInFile,
    int TagsInFile,
    int ProblemTagLinksInFile,
    int AttachmentsInFile,
    int MissingAttachmentFiles,
    int ImportedCount,
    int SkippedCount,
    int ConflictCount,
    IReadOnlyList<string> Errors)
{
    public int ProblemsImportedCount { get; init; }
    public int TagsImportedCount { get; init; }
    public int AttachmentsImportedCount { get; init; }
    public int ProblemTagsAppliedCount { get; init; }
}
