namespace FieldKb.Domain.Models;

public sealed record Tag(
    string Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedByInstanceId,
    bool IsDeleted
);

