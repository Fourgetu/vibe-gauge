namespace VibeGauge.Proxy.Health;

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

    public object Snapshot() => new
    {
        status = "ok",
        product = "VibeGauge.Proxy",
        version = typeof(ProxyHealth).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        port,
        upstream = Uri.TryCreate(route, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : route ?? "system",
        upstream_error = routeError,
        uptimeSeconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
        requests = Interlocked.Read(ref requests),
        parsed = Interlocked.Read(ref parsed),
        errors = Interlocked.Read(ref errors)
    };
}
