using System.Text;
using System.Text.Json;
using VibeGauge.Proxy.Health;
using VibeGauge.Proxy.Security;
using VibeGauge.Proxy.Usage;

namespace VibeGauge.Proxy;

public static class ProxySelfTest
{
    public static Task RunAsync()
    {
        var openAi = new UsageAccumulator("deepseek-chat");
        UsageParser.Apply(Encoding.UTF8.GetBytes(
            """{"model":"deepseek-chat","usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":60},"completion_tokens_details":{"reasoning_tokens":4}}}"""), openAi);
        Require(openAi.Parsed && openAi.ContextTokens == 100 && openAi.CacheReadTokens == 60 &&
                openAi.OutputTokens == 20 && openAi.ThinkingTokens == 4, "OpenAI usage");

        var anthropic = new UsageAccumulator("glm-4.7");
        UsageParser.Apply(Encoding.UTF8.GetBytes(
            """{"type":"message","model":"glm-4.7","usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":2,"output_tokens":7}}"""), anthropic);
        Require(anthropic.Parsed && anthropic.ContextTokens == 17 && anthropic.CacheReadTokens == 5 &&
                anthropic.CacheWriteTokens == 2 && anthropic.OutputTokens == 7, "Anthropic usage");

        var healthJson = JsonSerializer.Serialize(new ProxyHealth(18790).Snapshot(), ProxyJsonContext.Default.ProxyHealthSnapshot);
        Require(healthJson.Contains("\"product\":\"VibeGauge.Proxy\"", StringComparison.Ordinal), "health json");
        var usageJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new UsageRecord
        {
            Timestamp = "2026-01-01T00:00:00Z", Epoch = 1, Host = "api.openai.com", Provider = "OpenAI", Path = "/v1/responses",
            Model = "gpt-5", Stream = false, Status = 200, Milliseconds = 12, ContextTokens = 10, CacheReadTokens = 2,
            CacheWriteTokens = 0, OutputTokens = 5, ThinkingTokens = 0, Parsed = true
        }, ProxyJsonContext.Default.UsageRecord));
        Require(usageJson.Contains("\"ctx\":10", StringComparison.Ordinal), "usage json");

        const string secret = "SUPER_SECRET_KEY_12345678901234567890";
        Require(!SecretRedactor.Redact("Authorization: Bearer " + secret).Contains(secret, StringComparison.Ordinal), "redaction");
        Console.WriteLine("VibeGauge Proxy self-test passed");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Proxy self-test failed: " + name);
    }
}
