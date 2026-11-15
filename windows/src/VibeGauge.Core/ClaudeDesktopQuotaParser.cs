using System.Globalization;
using System.Text.Json;

namespace VibeGauge.Core;

public static class ClaudeDesktopQuotaParser
{
    public static ClaudeDesktopQuotaResult Parse(JsonElement root, DateTimeOffset captured)
    {
        if (root.ValueKind != JsonValueKind.Object) return new(Error: "Claude 额度响应格式不支持");
        QuotaWindow? Window(string name, TimeSpan duration)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number ||
                !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0) return null;
            DateTimeOffset? reset = value.TryGetProperty("resets_at", out var at) && at.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            return new((int)Math.Ceiling(Math.Clamp(percent, 0, 100)), reset, captured, duration);
        }
        var five = Window("five_hour", TimeSpan.FromHours(5));
        var week = Window("seven_day", TimeSpan.FromDays(7));
        var extra = new List<NamedQuota>();
        foreach (var (key, label) in new[] { ("seven_day_sonnet", "Sonnet · 周"), ("seven_day_opus", "Opus · 周"),
            ("seven_day_oauth_apps", "OAuth Apps · 周"), ("seven_day_cowork", "Cowork · 周") })
            if (Window(key, TimeSpan.FromDays(7)) is { } window) extra.Add(new(label, window));
        return five is null && week is null && extra.Count == 0 ? new(Error: "官方尚未返回可用额度窗口") : new(five, week, extra);
    }
}
