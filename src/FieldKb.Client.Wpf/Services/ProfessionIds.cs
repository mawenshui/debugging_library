namespace FieldKb.Client.Wpf;

public static class ProfessionIds
{
    public const string General = "general";
    public const string Hardware = "hardware";
    public const string Software = "software";
    public const string Embedded = "embedded";
    public const string Ui = "ui";
    public const string Qa = "qa";
    public const string Ops = "ops";

    public static readonly IReadOnlyList<ProfessionOption> Options = new[]
    {
        new ProfessionOption(General, "通用（默认）"),
        new ProfessionOption(Hardware, "硬件工程师"),
        new ProfessionOption(Software, "软件工程师"),
        new ProfessionOption(Embedded, "嵌入式工程师"),
        new ProfessionOption(Ui, "UI 工程师"),
        new ProfessionOption(Qa, "测试/QA"),
        new ProfessionOption(Ops, "运维/现场实施")
    };

    public static string Normalize(string? professionId)
    {
        var p = (professionId ?? string.Empty).Trim().ToLowerInvariant();
        return IsValid(p) ? p : General;
    }

    public static bool IsValid(string? professionId)
    {
        return professionId is not null
            && (string.Equals(professionId, General, StringComparison.Ordinal)
                || string.Equals(professionId, Hardware, StringComparison.Ordinal)
                || string.Equals(professionId, Software, StringComparison.Ordinal)
                || string.Equals(professionId, Embedded, StringComparison.Ordinal)
                || string.Equals(professionId, Ui, StringComparison.Ordinal)
                || string.Equals(professionId, Qa, StringComparison.Ordinal)
                || string.Equals(professionId, Ops, StringComparison.Ordinal));
    }
}

public sealed record ProfessionOption(string Id, string DisplayName);

