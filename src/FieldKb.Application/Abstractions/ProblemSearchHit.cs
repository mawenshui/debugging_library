namespace FieldKb.Application.Abstractions;

public sealed record ProblemSearchHit(
    string ProblemId,
    string Title,
    DateTimeOffset UpdatedAtUtc,
    double Score,
    string? Snippet
);
