using System.Text.RegularExpressions;

namespace VibeGauge.Core;

public sealed record LocalRuntime(string Name, int Port, int ProcessId);
public static class LocalRuntimeDiscovery
{
    public static IReadOnlyList<LocalRuntime> From(IEnumerable<ProcessSnapshot> processes)
    {
        var result = new List<LocalRuntime>();
        foreach (var p in processes)
        {
            var name = Path.GetFileNameWithoutExtension(p.Name).ToLowerInvariant();
            if (name is "llama-server" or "llama-server-avx2" or "llama-server-avx512")
            {
                var match = Regex.Match(p.CommandLine, @"(?:^|\s)(?:--port|-p)(?:=|\s+)""?(\d+)", RegexOptions.None, TimeSpan.FromMilliseconds(50));
                var port = match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : 8080;
                if (port is >= 1024 and <= 65535) result.Add(new("llama.cpp", port, p.ProcessId));
            }
            else if (name is "lm studio" or "lm-studio" or "llmster") result.Add(new("LM Studio", 1234, p.ProcessId));
        }
        return result.DistinctBy(x => (x.Name, x.Port)).OrderBy(x => x.Port).Take(16).ToArray();
    }
}
