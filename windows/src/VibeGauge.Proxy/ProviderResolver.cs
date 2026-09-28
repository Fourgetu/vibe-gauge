namespace VibeGauge.Proxy;

public static class ProviderResolver
{
    private static readonly (string Host, string Name)[] Known =
    [
        ("api.deepseek.com", "DeepSeek"),
        ("openrouter.ai", "OpenRouter"),
        ("api.anthropic.com", "Anthropic"),
        ("open.bigmodel.cn", "GLM"),
        ("api.z.ai", "GLM"),
        ("api.openai.com", "OpenAI"),
        ("api.x.ai", "xAI"),
        ("moonshot.cn", "Kimi"),
        ("moonshot.ai", "Kimi")
    ];

    public static string ForHost(string host)
    {
        var value = host.ToLowerInvariant().TrimEnd('.');
        foreach (var item in Known)
            if (value == item.Host || value.EndsWith("." + item.Host, StringComparison.Ordinal)) return item.Name;
        return value.StartsWith("api.", StringComparison.Ordinal) && value.Count(x => x == '.') >= 2 ? value[4..] : value;
    }
}
