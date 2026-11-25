using System.Text.Json.Serialization;
using VibeGauge.Proxy.Health;
using VibeGauge.Proxy.Usage;

namespace VibeGauge.Proxy;

public sealed record ProxyStatusResponse([property: JsonPropertyName("status")] string Status);
public sealed record ProxyErrorResponse([property: JsonPropertyName("error")] string Error);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(ProxyHealthSnapshot))]
[JsonSerializable(typeof(ProxyStatusResponse))]
[JsonSerializable(typeof(ProxyErrorResponse))]
[JsonSerializable(typeof(UsageRecord))]
public partial class ProxyJsonContext : JsonSerializerContext;
