using System.Text;
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
