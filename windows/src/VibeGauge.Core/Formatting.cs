using System.Globalization;
using System.Text.RegularExpressions;

namespace VibeGauge.Core;

public enum TokenUnit { Chinese, International }

public static partial class Formatting
{
    public static string Tokens(long value, TokenUnit unit = TokenUnit.Chinese) => unit == TokenUnit.International ? value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000m:0.##} B",
        >= 1_000_000 => $"{value / 1_000_000m:0.##} M",
        >= 1_000 => $"{value / 1_000m:0.###} K",
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    } : value switch
    {
        >= 100_000_000 => $"{value / 100_000_000d:0.00} 亿",
        >= 10_000 => $"{value / 10_000d:0.0} 万",
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };

    public static string Countdown(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "查不到";
        var left = reset.Value - now;
        if (left <= TimeSpan.Zero) return "已重置";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d{left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h{left.Minutes:00}m";
        return $"{Math.Max(1, (int)left.TotalMinutes)}m";
    }

    public static string ModelDisplayName(string raw)
    {
        var value = raw.ToLowerInvariant();
        if (value.Contains("opus-5")) return "Opus 5";
        if (value.Contains("fable-5-1")) return "Fable 5.1";
        var match = ClaudeModelRegex().Match(value);
        if (match.Success)
        {
            var family = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups[1].Value);
            return $"{family} {match.Groups[2].Value}.{match.Groups[3].Value}";
        }
        return raw;
    }

    public static string CodexPlanLabel(string raw) => raw.ToLowerInvariant() switch
    {
        "prolite" => "Pro Lite",
        "pro" => "Pro",
        "plus" => "Plus",
        "go" => "Go",
        "team" or "business" => "Team",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        "free" => "Free",
        "" => "",
        _ => char.ToUpperInvariant(raw[0]) + raw[1..]
    };

    public static DateTimeOffset? ParseDate(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    [GeneratedRegex(@"claude-(opus|sonnet|haiku)-(\d+)-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeModelRegex();
}
