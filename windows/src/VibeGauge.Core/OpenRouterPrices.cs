using System.Globalization;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed record OpenRouterPriceSnapshot(string Json, int ModelCount, int AliasCount, DateTimeOffset CapturedAt);

public static class OpenRouterPrices
{
    // The catalogue quotes USD per token; VibeGauge's local table uses USD per million.
    // Only base text-token rates are imported, not endpoint-specific or tiered charges.
    public static OpenRouterPriceSnapshot Parse(string json, DateTimeOffset capturedAt)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid OpenRouter model catalogue.");
        var rows = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = item.StringOrEmpty("id");
            if (id.Length is 0 or > 200 || id.Any(char.IsControl) || !id.Contains('/') || !ids.Add(id)) continue;
            if (!item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object) continue;
            decimal? Rate(string key)
            {
                if (!pricing.TryGetProperty(key, out var v)) return null;
                var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 &&
                    value <= decimal.MaxValue / 1_000_000 ? value * 1_000_000 : null;
            }
            if (Rate("prompt") is not { } input || Rate("completion") is not { } output) continue;
            var read = Rate("input_cache_read"); var write = Rate("input_cache_write");
            if (pricing.TryGetProperty("input_cache_read", out _) && read is null ||
                pricing.TryGetProperty("input_cache_write", out _) && write is null) continue;
            rows[id] = new() { ["in"] = input, ["out"] = output, ["cache_read"] = read ?? input, ["cache_write"] = write ?? input };
        }
        if (rows.Count == 0) throw new InvalidDataException("OpenRouter returned no usable prices.");
        var outputRows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["_source"] = "OpenRouter", ["_currency"] = "USD", ["_match"] = "exact",
            ["_strip_suffixes"] = new[] { "-basispoints" },
            ["_asof"] = capturedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["_fetched_at"] = capturedAt.ToString("O", CultureInfo.InvariantCulture),
            ["_basis"] = "Current base text-token rates only; excludes tier overrides, tools, images, audio, cache storage and historical price changes. Missing cache rates use the prompt rate."
        };
        foreach (var (id, rate) in rows.OrderBy(x => x.Key, StringComparer.Ordinal)) outputRows[id] = rate;
        var aliases = 0;
        // Unique provider-free IDs are safe aliases. Never guess custom suffixes or
        // conflate two providers that happen to publish the same short model name.
        foreach (var group in ids.GroupBy(id => id[(id.IndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase))
            if (group.Count() == 1 && rows.TryGetValue(group.Single(), out var rate) && !outputRows.ContainsKey(group.Key))
            { outputRows[group.Key] = rate; aliases++; }
        return new(JsonSerializer.Serialize(outputRows, new JsonSerializerOptions { WriteIndented = true }), rows.Count, aliases, capturedAt);
    }

    public static string Save(AppPaths paths, OpenRouterPriceSnapshot snapshot)
    {
        var path = paths.ResolveOwnDataFile("prices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = "";
        try
        {
            File.WriteAllText(temporary, snapshot.Json);
            if (File.Exists(path))
            {
                backup = path + ".before-openrouter-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".bak";
                File.Copy(path, backup);
            }
            File.Move(temporary, path, true);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
