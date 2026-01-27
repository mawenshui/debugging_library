namespace FieldKb.Client.Wpf;

public sealed record ProfessionProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<FixedFieldDefinition> FixedFields,
    IReadOnlyList<string> CommonKeys,
    IReadOnlyList<EnvironmentEntry> DefaultCustomEntries
);

public sealed record FixedFieldDefinition(
    string Key,
    string Label,
    FixedFieldValidation Validation,
    bool IsRequired = false,
    string? Placeholder = null
);

public enum FixedFieldValidation
{
    None = 0,
    IpAddress = 1,
    Port = 2
}

