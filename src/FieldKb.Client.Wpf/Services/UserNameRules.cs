using System.Text.RegularExpressions;

namespace FieldKb.Client.Wpf;

public static partial class UserNameRules
{
    public const int MaxLength = 32;

    public static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    public static bool IsValid(string? value, out string? error)
    {
        var v = Normalize(value);

        if (string.IsNullOrWhiteSpace(v))
        {
            error = "用户名不能为空。";
            return false;
        }

        if (v.Length > MaxLength)
        {
            error = $"用户名不能超过 {MaxLength} 个字符。";
            return false;
        }

        if (!AllowedCharsRegex().IsMatch(v))
        {
            error = "用户名只能包含中文/字母/数字/空格/下划线/短横线/点。";
            return false;
        }

        error = null;
        return true;
    }

    [GeneratedRegex(@"^[\p{L}\p{N} _\.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedCharsRegex();
}

