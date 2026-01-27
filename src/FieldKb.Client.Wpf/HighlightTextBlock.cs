using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FieldKb.Client.Wpf;

public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty TermsTextProperty = DependencyProperty.Register(
        nameof(TermsText),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty HighlightBackgroundProperty = DependencyProperty.Register(
        nameof(HighlightBackground),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty HighlightForegroundProperty = DependencyProperty.Register(
        nameof(HighlightForeground),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnChanged));

    public string SourceText
    {
        get => GetValue(SourceTextProperty) as string ?? string.Empty;
        set => SetValue(SourceTextProperty, value);
    }

    public string TermsText
    {
        get => GetValue(TermsTextProperty) as string ?? string.Empty;
        set => SetValue(TermsTextProperty, value);
    }

    public Brush? HighlightBackground
    {
        get => (Brush?)GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    public Brush? HighlightForeground
    {
        get => (Brush?)GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HighlightTextBlock)d).UpdateInlines();
    }

    private void UpdateInlines()
    {
        Inlines.Clear();

        var text = SourceText ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        var terms = SplitTerms(TermsText);
        if (terms.Length == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var pattern = string.Join("|", terms.Select(Regex.Escape));
        if (string.IsNullOrWhiteSpace(pattern))
        {
            Inlines.Add(new Run(text));
            return;
        }

        var bg = HighlightBackground ?? TryFindResource("Brush.Selection") as Brush;
        var fg = HighlightForeground ?? Foreground;

        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var lastIndex = 0;
        foreach (Match m in regex.Matches(text))
        {
            if (!m.Success || m.Length <= 0)
            {
                continue;
            }

            if (m.Index > lastIndex)
            {
                Inlines.Add(new Run(text.Substring(lastIndex, m.Index - lastIndex)));
            }

            var run = new Run(text.Substring(m.Index, m.Length));
            if (bg is not null)
            {
                run.Background = bg;
            }

            if (fg is not null)
            {
                run.Foreground = fg;
            }

            Inlines.Add(run);
            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < text.Length)
        {
            Inlines.Add(new Run(text.Substring(lastIndex)));
        }
    }

    private static string[] SplitTerms(string? text)
    {
        return (text ?? string.Empty)
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }
}
