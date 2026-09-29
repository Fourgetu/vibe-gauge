using System.Text.Json;

namespace VibeGauge.Core;

public static class LocalModelParser
{
    public static IReadOnlyList<string>? LmStudio(JsonElement root, bool legacy = false)
    {
        if (!root.TryGetProperty(legacy ? "data" : "models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        return models.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && (legacy
                ? x.StringOrEmpty("state") == "loaded"
                : x.TryGetProperty("loaded_instances", out var instances) && instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0))
            .Select(x => x.StringOrEmpty(legacy ? "id" : "key") is { Length: > 0 } key ? key : x.StringOrEmpty("id"))
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }
}
