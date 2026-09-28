using System.Text.Json;

namespace VibeGauge.Proxy.Usage;

public static class UsageParser
{
    public static void Apply(ReadOnlySpan<byte> json, UsageAccumulator usage)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
            if (type is "response.completed" or "response.incomplete" or "response.failed" && root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
                root = response;
            if (type is "error" or "response.incomplete" or "response.failed" || root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
                usage.Error = "upstream_response_error";
            _ = OpenAiUsageParser.Apply(root, usage);
            _ = AnthropicUsageParser.Apply(root, usage);
            if (root.TryGetProperty("usageMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                if (OpenAiUsageParser.TryLong(metadata, "promptTokenCount", out var input)) { usage.ContextTokens = input; usage.Parsed = true; }
                if (OpenAiUsageParser.TryLong(metadata, "candidatesTokenCount", out var candidates)) usage.GeminiCandidates = candidates;
                if (OpenAiUsageParser.TryLong(metadata, "thoughtsTokenCount", out var thinking)) usage.ThinkingTokens = thinking;
                if (OpenAiUsageParser.TryLong(metadata, "cachedContentTokenCount", out var cached)) usage.CacheReadTokens = cached;
                usage.OutputTokens = usage.GeminiCandidates + usage.ThinkingTokens;
                if (root.TryGetProperty("modelVersion", out var model) && model.ValueKind == JsonValueKind.String) usage.SetResponseModel(model.GetString());
            }
            if (OpenAiUsageParser.TryLong(root, "prompt_eval_count", out var prompt)) { usage.ContextTokens = prompt; usage.Parsed = true; }
            if (OpenAiUsageParser.TryLong(root, "eval_count", out var output)) { usage.OutputTokens = output; usage.Parsed = true; }
        }
        catch (JsonException) { usage.Error ??= "invalid_usage_json"; }
    }
}
