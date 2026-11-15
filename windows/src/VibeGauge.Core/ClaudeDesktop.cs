using System.Text.Json;

namespace VibeGauge.Core;

public static class ClaudeDesktop
{
    public const string Name = "Claude Desktop";

    public static PlatformStatus? Read(AppPaths paths, ProcessReport processes)
    {
        // Prefer the running profile. Never substitute another profile's cached account.
        var active = (processes.ProviderProcesses ?? []).Where(p => p.Provider == Name)
            .Select(p => p.DataDirectory).Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var configs = active.Length > 0 ? active.Select(p => Path.Combine(p, "config.json")).ToArray()
            : KnownProfiles(paths).Select(p => Path.Combine(p, "config.json")).Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var running = processes.ClaudeDesktopProcesses > 0;
        if (!running && configs.Length == 0) return null;

        var state = ProviderDataState.AuthenticationUnknown;
        var tier = "未知";
        var detail = "登录状态未知";
        if (configs.Length == 1)
        {
            using var doc = JsonSupport.ReadDocument(configs[0]);
            if (doc?.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("windowSizeWasSignedIn", out var signedIn) &&
                signedIn.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                // Desktop persists this boolean on sign-in/sign-out. It is a local
                // observation, not server validation. Account IDs can survive logout.
                var loggedIn = signedIn.GetBoolean();
                state = loggedIn ? ProviderDataState.NoQuota : ProviderDataState.NotSignedIn;
                tier = loggedIn ? "已登录" : "未登录";
                detail = loggedIn ? "已登录（本地状态）" : "未登录（本地状态）";
            }
            else if (doc is null && File.Exists(configs[0]))
                detail = "登录状态未知：桌面配置暂时无法读取";
        }
        else if (configs.Length > 1)
            detail = "登录状态未知：检测到多个桌面配置";

        return new(Name, tier, running, processes.ClaudeDesktopProcesses, state,
            detail + "\n本地登录标记不代表服务器验证结果；官方额度需可用的桌面账号授权。",
            CompactDetail: detail,
            Metadata: new([new("状态来源", "Claude Desktop 本地配置")], configs));
    }

    private static IEnumerable<string> KnownProfiles(AppPaths paths)
    {
        yield return Path.Combine(paths.WindowsRoamingDataRoot, "Claude");
        // Current Store profile, followed by the pre-migration Store location.
        yield return Path.Combine(paths.WindowsLocalDataRoot, "Claude-Data", "claude_pzs8sxrjxfjjc");
        yield return Path.Combine(paths.WindowsLocalDataRoot, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
    }
}
