using System.Text.Json.Serialization;

namespace VibeGauge.Proxy.Usage;

public sealed record UsageRecord
{
    [JsonPropertyName("ts")] public required string Timestamp { get; init; }
    [JsonPropertyName("epoch")] public required double Epoch { get; init; }
    [JsonPropertyName("host")] public required string Host { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("stream")] public required bool Stream { get; init; }
    [JsonPropertyName("status")] public required int Status { get; init; }
    [JsonPropertyName("ms")] public required long Milliseconds { get; init; }
    [JsonPropertyName("ctx")] public required long ContextTokens { get; init; }
    [JsonPropertyName("cache_read")] public required long CacheReadTokens { get; init; }
    [JsonPropertyName("cache_write")] public required long CacheWriteTokens { get; init; }
    [JsonPropertyName("out")] public required long OutputTokens { get; init; }
    [JsonPropertyName("think")] public required long ThinkingTokens { get; init; }
    [JsonPropertyName("parsed")] public required bool Parsed { get; init; }
    [JsonPropertyName("key")] public string KeyFingerprint { get; init; } = "";
    [JsonPropertyName("reached_upstream")] public bool ReachedUpstream { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("rl")] public IReadOnlyDictionary<string, string>? RateLimits { get; init; }
    [JsonPropertyName("complete")] public bool Complete { get; init; }
}

public sealed class UsageAccumulator(string? requestModel)
{
    public string Model { get; private set; } = string.IsNullOrWhiteSpace(requestModel) ? "Unknown" : requestModel;
    public bool HasRequestModel { get; } = !string.IsNullOrWhiteSpace(requestModel);
    public long ContextTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ThinkingTokens { get; set; }
    public long AnthropicInputTokens { get; set; }
    public bool Parsed { get; set; }
    public string? Error { get; set; }
    public long GeminiCandidates { get; set; }

    public void SetResponseModel(string? model)
    {
        if (!HasRequestModel && !string.IsNullOrWhiteSpace(model)) Model = model;
    }
}
