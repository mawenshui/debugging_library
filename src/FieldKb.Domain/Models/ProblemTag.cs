namespace FieldKb.Domain.Models;

public sealed record ProblemTag(
    string Id,
    string ProblemId,
    string TagId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedByInstanceId,
    bool IsDeleted
);

