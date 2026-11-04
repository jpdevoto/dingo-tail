// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace DingoTail;

internal sealed record HighlightRule(string Name, string Pattern, bool Enabled, bool Regex, bool CaseSensitive,
    int Foreground, int Background)
{
    public static List<HighlightRule> Defaults() =>
    [
        new("ERROR", "ERROR", true, false, false, 0xff8282, 0x12151b),
        new("WARN", "WARN", true, false, false, 0xf0c46f, 0x12151b),
        new("INFO", "INFO", true, false, false, 0x89c0f4, 0x12151b),
        new("DEBUG", "DEBUG", true, false, false, 0xa2adc0, 0x12151b),
    ];
}

internal sealed class HighlightPalette
{
    internal readonly record struct RowStyle(Color Foreground, Color Background);
    private readonly List<(HighlightRule Rule, Regex? Regex, RowStyle Style)> rules = [];

    public HighlightPalette(IEnumerable<HighlightRule> entries)
    {
        foreach (var rule in entries)
        {
            if (rule is null || string.IsNullOrEmpty(rule.Pattern) ||
                rule.Foreground is < 0 or > 0xffffff || rule.Background is < 0 or > 0xffffff)
                throw new ArgumentException("Highlighting rules need a pattern and two RGB colors.");
            try
            {
                var regex = rule.Regex ? new Regex(rule.Pattern,
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking |
                    (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                    TimeSpan.FromMilliseconds(10)) : null;
                if (rule.Enabled)
                    rules.Add((rule, regex, new(ToColor(rule.Foreground), ToColor(rule.Background))));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException($"Rule “{rule.Name}”: {error.Message}", error);
            }
        }
    }

    public RowStyle? Match(string text)
    {
        foreach (var (rule, regex, style) in rules)
        {
            try
            {
                if (regex?.IsMatch(text) ?? text.Contains(rule.Pattern,
                    rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                    return style;
            }
            catch (RegexMatchTimeoutException)
            {
                // Keep processing subsequent rules when this row exceeds the matching budget.
            }
        }
        return null;
    }

    private static Color ToColor(int rgb) => Color.FromArgb(rgb | unchecked((int)0xff000000));
}
