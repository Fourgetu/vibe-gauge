using System.Text.Json;

namespace VibeGauge.Proxy.Usage;

public static class OpenAiUsageParser
{
    public static bool Apply(JsonElement root, UsageAccumulator usage)
    {
        if (!root.TryGetProperty("usage", out var value) || value.ValueKind != JsonValueKind.Object) return false;
        var hasInput = TryLong(value, "prompt_tokens", out var input) || TryLong(value, "input_tokens", out input);
        var hasOutput = TryLong(value, "completion_tokens", out var output) || TryLong(value, "output_tokens", out output);
        if (!hasInput && !hasOutput) return false;
        if (hasInput) usage.ContextTokens = input;
        if (hasOutput) usage.OutputTokens = output;
        if (value.TryGetProperty("prompt_tokens_details", out var promptDetails) ||
            value.TryGetProperty("input_tokens_details", out promptDetails))
            if (TryLong(promptDetails, "cached_tokens", out var cached)) usage.CacheReadTokens = cached;
        if (value.TryGetProperty("completion_tokens_details", out var completionDetails) ||
            value.TryGetProperty("output_tokens_details", out completionDetails))
            if (TryLong(completionDetails, "reasoning_tokens", out var reasoning)) usage.ThinkingTokens = reasoning;
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            usage.SetResponseModel(model.GetString());
        usage.Parsed = true;
        return true;
    }

    internal static bool TryLong(JsonElement root, string property, out long value)
    {
        value = 0;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var item) &&
               item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out value) && value >= 0;
    }
}
