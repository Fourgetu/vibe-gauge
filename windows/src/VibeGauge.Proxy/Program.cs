using VibeGauge.Proxy;
using VibeGauge.Core;
using System.Text.Json.Nodes;

if (args.Contains("--vibegauge-hook=claude") || args.Contains("--vibegauge-statusline=claude"))
{
    try
    {
        var raw = await Console.In.ReadToEndAsync();
        if (raw.Length > 4 * 1024 * 1024) return;
        var payload = JsonNode.Parse(raw) as JsonObject;
        if (payload is null) return;
        var paths = new AppPaths();
        if (args.Contains("--vibegauge-hook=claude")) CliBridge.RecordHook(paths, payload, DateTimeOffset.Now);
        else Console.WriteLine(CliBridge.RecordStatus(paths, payload, DateTimeOffset.Now));
    }
    catch { /* Observation hooks must never block, approve, or change a CLI request. */ }
    return;
}

var options = ProxyOptions.Parse(args);
if (options.SelfTest)
{
    await ProxySelfTest.RunAsync();
    return;
}

await using var proxy = new ProxyHost(options);
await proxy.RunAsync();
