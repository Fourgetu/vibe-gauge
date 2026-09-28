using System.Text.Json;

namespace VibeGauge.Core;

public static class PiDesktopUsage
{
    public const string SourceName = "PI-Desktop";

    public static InteractionRecord? Parse(JsonElement root)
    {
        var compaction = root.StringOrEmpty("type") == "compaction";
        if (!compaction && (root.StringOrEmpty("type") != "message" || root.StringOrEmpty("role") != "assistant"))
            return null;
        var metadata = root;
        if (!compaction && (!root.TryGetProperty("meta", out metadata) || metadata.ValueKind != JsonValueKind.Object))
            return null;
        if (metadata.StringOrEmpty("status") is "streaming" or "pending" ||
            !metadata.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var id = root.StringOrEmpty("id");
        var at = Formatting.ParseDate(root.StringOrEmpty("createdAt"));
        if (id.Length == 0 || at is null ||
            !Read(usage, compaction ? "input" : "inputTokens", out var input) ||
            !Read(usage, compaction ? "output" : "outputTokens", out var output)) return null;
        Read(usage, compaction ? "cacheRead" : "cacheReadTokens", out var cacheRead);
        Read(usage, compaction ? "cacheWrite" : "cacheWriteTokens", out var cacheWrite);
        Read(usage, compaction ? "reasoning" : "reasoningTokens", out var reasoning);
        try
        {
            // PI's normalized input excludes cache; reasoning is already part of output.
            var context = checked(input + cacheRead + cacheWrite);
            if (checked(context + output) == 0) return null;
            var model = metadata.StringOrEmpty("modelId");
            return new(id, SourceName, model.Length > 0 ? model : SourceName, at.Value,
                context, cacheRead, cacheWrite, output, reasoning);
        }
        catch (OverflowException) { return null; }
    }

    private static bool Read(JsonElement usage, string key, out long value)
    {
        value = 0;
        if (!usage.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.Number ||
            !item.TryGetInt64(out var number) || number < 0) return false;
        value = number;
        return true;
    }
}
