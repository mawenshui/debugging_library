using System.Text.Json.Serialization;

namespace FieldKb.Infrastructure.ImportExport;

public sealed record ChangeLine<T>(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("entity")] T Entity
);

