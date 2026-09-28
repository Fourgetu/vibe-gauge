using System.Text.Json;

namespace VibeGauge.Proxy.Usage;

public static class AnthropicUsageParser
{
    public static bool Apply(JsonElement root, UsageAccumulator usage)
    {
        JsonElement message = default;
        var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString() : null;
        var hasMessage = type == "message_start" && root.TryGetProperty("message", out message) ||
                         type == "message" && (message = root).ValueKind == JsonValueKind.Object;
        var applied = false;
        if (hasMessage)
        {
            if (message.TryGetProperty("usage", out var messageUsage)) applied |= ApplyUsage(messageUsage, usage);
            if (message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                usage.SetResponseModel(model.GetString());
        }
        if (type == "message_delta" && root.TryGetProperty("usage", out var deltaUsage))
            applied |= ApplyUsage(deltaUsage, usage);
        return applied;
    }

    private static bool ApplyUsage(JsonElement value, UsageAccumulator usage)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        if (OpenAiUsageParser.TryLong(value, "input_tokens", out var input))
        {
            usage.AnthropicInputTokens = input;
            found = true;
        }
        if (OpenAiUsageParser.TryLong(value, "cache_read_input_tokens", out var read))
        {
            usage.CacheReadTokens = read;
            found = true;
        }
        if (OpenAiUsageParser.TryLong(value, "cache_creation_input_tokens", out var write))
        {
            usage.CacheWriteTokens = write;
            found = true;
        }
        if (OpenAiUsageParser.TryLong(value, "output_tokens", out var output))
        {
            usage.OutputTokens = output;
            found = true;
        }
        if (found)
        {
            usage.ContextTokens = usage.AnthropicInputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;
            usage.Parsed = true;
        }
        return found;
    }
}
