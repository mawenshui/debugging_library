using FieldKb.Application.ImportExport;

namespace FieldKb.Infrastructure.Storage;

public sealed record InstanceIdentity(
    string InstanceId,
    InstanceKind Kind,
    DateTimeOffset CreatedAtUtc
);

