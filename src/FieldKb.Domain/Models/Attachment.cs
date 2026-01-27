namespace FieldKb.Domain.Models;

public sealed record Attachment(
    string Id,
    string ProblemId,
    string OriginalFileName,
    string ContentHash,
    long SizeBytes,
    string MimeType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedByInstanceId,
    bool IsDeleted
);

