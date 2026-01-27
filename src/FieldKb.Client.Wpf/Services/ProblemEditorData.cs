namespace FieldKb.Client.Wpf;

public sealed record EnvironmentEntry(string Key, string Value);

public sealed record TagOption(string TagId, string Name, bool IsSelected);

public sealed record ProblemEditorData(
    string Title,
    string Symptom,
    string RootCause,
    string Solution,
    string EnvironmentJson,
    IReadOnlyList<TagOption> Tags
);
