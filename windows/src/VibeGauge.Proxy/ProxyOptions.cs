using VibeGauge.Core;

namespace VibeGauge.Proxy;

public sealed record ProxyOptions
{
    public int Port { get; init; } = 18790;
    public string DataDirectory { get; init; } = new AppPaths().LocalDataRoot;
    public string ControlToken { get; init; } = "";
    public bool AllowLoopbackUpstream { get; init; }
    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public long MaxRequestBodyBytes { get; init; } = 32L * 1024 * 1024;
    public bool SelfTest { get; init; }
    public string? UpstreamProxy { get; init; }
    public string[] NoProxy { get; init; } = [];
    public string UpstreamError { get; init; } = "";

    public string CallsPath => Path.Combine(DataDirectory, "api-calls.jsonl");

    public static ProxyOptions Parse(IReadOnlyList<string> args)
    {
        var directory = ReadString(args, "--data-dir=") ?? Environment.GetEnvironmentVariable("VIBEGAUGE_DIR") ?? new AppPaths().LocalDataRoot;
        var config = ProxyConfiguration.Load(directory);
        var route = config.Upstream ?? Environment.GetEnvironmentVariable("VIBEGAUGE_UPSTREAM_PROXY");
        var options = new ProxyOptions
        {
            Port = ReadInt(args, "--port=", Environment.GetEnvironmentVariable("VIBEGAUGE_PROXY_PORT"), config.Port),
            DataDirectory = directory,
            UpstreamProxy = ProxyConfiguration.ValidRoute(route) ? route : "direct",
            NoProxy = config.NoProxy ?? [],
            UpstreamError = config.Error.Length > 0 ? config.Error : ProxyConfiguration.ValidRoute(route) ? "" : "invalid VIBEGAUGE_UPSTREAM_PROXY",
            ControlToken = ReadString(args, "--control-token=") ?? "",
            AllowLoopbackUpstream = args.Contains("--allow-loopback-upstream", StringComparer.OrdinalIgnoreCase),
            SelfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase)
        };
        var rawTimeout = ReadString(args, "--timeout-ms=");
        var timeout = int.TryParse(rawTimeout, out var value) ? Math.Clamp(value, 100, 3600000) : (int)options.UpstreamTimeout.TotalMilliseconds;
        return options with { UpstreamTimeout = TimeSpan.FromMilliseconds(timeout) };
    }

    private static string? ReadString(IReadOnlyList<string> args, string prefix) =>
        args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    private static int ReadInt(IReadOnlyList<string> args, string prefix, string? environment, int fallback)
    {
        var raw = ReadString(args, prefix) ?? environment;
        return int.TryParse(raw, out var value) && value is > 0 and <= 65535 ? value : fallback;
    }
}
