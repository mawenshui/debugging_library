namespace FieldKb.Domain.Models;

public sealed record Problem(
    string Id,
    string Title,
    string Symptom,
    string RootCause,
    string Solution,
    string EnvironmentJson,
    int Severity,
    int Status,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedByInstanceId,
    bool IsDeleted,
    DateTimeOffset? DeletedAtUtc,
    SourceKind SourceKind
);

