using System.Text.Json.Serialization;

namespace FieldKb.Infrastructure.ImportExport;

public sealed record PackageManifest(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("exporterInstanceId")] string ExporterInstanceId,
    [property: JsonPropertyName("exporterKind")] string ExporterKind,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("baseWatermarkUtc")] DateTimeOffset? BaseWatermarkUtc,
    [property: JsonPropertyName("maxUpdatedAtUtc")] DateTimeOffset MaxUpdatedAtUtc,
    [property: JsonPropertyName("recordCounts")] IReadOnlyDictionary<string, int> RecordCounts,
    [property: JsonPropertyName("checksums")] IReadOnlyDictionary<string, string> Checksums
);

