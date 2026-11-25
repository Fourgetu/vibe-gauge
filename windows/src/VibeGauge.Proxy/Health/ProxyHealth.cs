using System.Text.Json.Serialization;

namespace VibeGauge.Proxy.Health;

public sealed record ProxyHealthSnapshot(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("upstream")] string Upstream,
    [property: JsonPropertyName("upstream_error")] string UpstreamError,
    [property: JsonPropertyName("uptimeSeconds")] long UptimeSeconds,
    [property: JsonPropertyName("requests")] long Requests,
    [property: JsonPropertyName("parsed")] long Parsed,
    [property: JsonPropertyName("errors")] long Errors);

public sealed class ProxyHealth(int port, string? route = null, string routeError = "")
{
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private long requests;
    private long parsed;
    private long errors;

    public void Record(bool wasParsed, bool error)
    {
        Interlocked.Increment(ref requests);
        if (wasParsed) Interlocked.Increment(ref parsed);
        if (error) Interlocked.Increment(ref errors);
    }

    public ProxyHealthSnapshot Snapshot() => new(
        "ok",
        "VibeGauge.Proxy",
        typeof(ProxyHealth).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        port,
        Uri.TryCreate(route, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : route ?? "system",
        routeError,
        Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
        Interlocked.Read(ref requests),
        Interlocked.Read(ref parsed),
        Interlocked.Read(ref errors));
}
