using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class OpenRouterPricesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vg-prices-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Catalogue = """
        {"data":[
          {"id":"vendor/model","pricing":{"prompt":"0.000002","completion":"0.00001","input_cache_read":"0.0000002","input_cache_write":"0.0000025","request":"9","overrides":[{"min_prompt_tokens":100,"prompt":"99"}]}},
          {"id":"vendor/model:free","pricing":{"prompt":"0","completion":"0"}},
          {"id":"vendor/no-cache","pricing":{"prompt":"1e-6","completion":"2e-6"}},
          {"id":"one/shared","pricing":{"prompt":"1e-6","completion":"1e-6"}},
          {"id":"two/shared","pricing":{"prompt":"2e-6","completion":"2e-6"}}
        ]}
        """;

    [Fact]
    public void RatesConvertToPerMillionAndExactAliasesDoNotGuessCustomModels()
    {
        var result = OpenRouterPrices.Parse(Catalogue, At);
        Assert.Equal(5, result.ModelCount); Assert.Equal(3, result.AliasCount);
        var table = PriceTable.Parse(result.Json);
        Assert.Equal("OpenRouter", table.Source); Assert.Equal("USD", table.Currency); Assert.Equal("2026-09-30", table.AsOf);
        Assert.True(table.ExactMatch);
        Assert.Equal(new PriceTable.Rate(2, 10, .2m, 2.5m), table.Rates["model"]);
        Assert.Equal(table.Rates["vendor/model"], table.Rates["model"]);
        Assert.Equal(2.79m, table.Cost("model", 1_000_000, 100_000, 200_000, 300_000));
        Assert.Equal(table.Cost("model", 1_000_000, 100_000, 200_000, 300_000), table.Cost("model-basispoints", 1_000_000, 100_000, 200_000, 300_000));
        Assert.Equal(table.Cost("vendor/model", 1, 1, 0, 0), table.Cost("vendor/model-basispoints", 1, 1, 0, 0));
        Assert.Null(table.Cost("model-basispoints-extra", 1, 1, 0, 0));
        Assert.Null(table.Cost("model-high", 1, 1, 0, 0));
        Assert.Null(table.Cost("shared", 1, 1, 0, 0));
        Assert.NotNull(table.Cost("one/shared", 1, 1, 0, 0));
        Assert.Equal(0m, table.Cost("model:free", 100, 100, 10, 10));
        Assert.Equal(new PriceTable.Rate(1, 2, 1, 1), table.Rates["no-cache"]);
        var estimate = table.Estimate([new("Codex", "model", 1, 1_000_000, 100_000, 200_000, 300_000)]);
        Assert.Contains("OpenRouter", estimate.Description);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("1e100")]
    [InlineData("")]
    public void InvalidRatesAreRejectedWithoutTurningIntoFreePrices(string value)
    {
        var json = JsonSerializer.Serialize(new { data = new[] { new { id = "vendor/bad", pricing = new { prompt = value, completion = "0" } } } });
        Assert.Throws<InvalidDataException>(() => OpenRouterPrices.Parse(json, At));
    }

    [Fact]
    public void InvalidCatalogueCannotReplaceOldFileAndValidUpdateBacksItUp()
    {
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        Directory.CreateDirectory(paths.LocalDataRoot);
        var file = Path.Combine(paths.LocalDataRoot, "prices.json");
        const string old = """{"manual":{"in":5,"out":10}}""";
        File.WriteAllText(file, old);
        Assert.Throws<InvalidDataException>(() => OpenRouterPrices.Parse("{\"data\":[]}", At));
        Assert.Equal(old, File.ReadAllText(file));
        var backup = OpenRouterPrices.Save(paths, OpenRouterPrices.Parse(Catalogue, At));
        Assert.Equal(old, File.ReadAllText(backup));
        Assert.Equal("OpenRouter", PriceTable.Load(paths).Source);
        Assert.Empty(Directory.GetFiles(paths.LocalDataRoot, "*.tmp"));
    }

    [Fact]
    public void InvalidSecondProviderStillPreventsAmbiguousShortAlias()
    {
        var json = """{"data":[{"id":"a/shared","pricing":{"prompt":"1","completion":"1"}},{"id":"b/shared","pricing":{}}]}""";
        var table = PriceTable.Parse(OpenRouterPrices.Parse(json, At).Json);
        Assert.Null(table.Cost("shared", 1, 1, 0, 0));
        Assert.NotNull(table.Cost("a/shared", 1, 1, 0, 0));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void AgentEffortIsPricedFromCurrentCatalogueAcrossClientsWithoutChangingTheModel()
    {
        static string Data(decimal input) => JsonSerializer.Serialize(new { data = new[] {
            new { id = "anthropic/claude-opus-5.5", pricing = new { prompt = input, completion = .000020m,
                input_cache_read = .0000002m, input_cache_write = .000005m } } } });
        var row = new ModelMix("Antigravity","claude-opus-5-5-medium",1,10_000,100,4_000);
        var table = PriceTable.Parse(OpenRouterPrices.Parse(Data(.000004m), At).Json);
        foreach (var (source,id) in new[]{("Antigravity",row.Model),("Devin","claude-opus-5-5-high"),
            ("PI-Desktop","Claude Opus 5.5 Medium"),("ZCode","anthropic/claude-opus-5-5-xhigh")})
        {
            var result = table.Estimate([row with { Source=source,Model=id }]);
            Assert.True(result.UsesBaseModelRate);
            Assert.Equal(0,result.UnpricedModels);
            Assert.Equal(.0268m,result.Amount);
        }
        var refreshed = PriceTable.Parse(OpenRouterPrices.Parse(Data(.000008m), At.AddDays(1)).Json);
        Assert.Equal(.0508m,refreshed.Estimate([row]).Amount);
        Assert.Equal(1,table.Estimate([row with { Source="API · example" }]).UnpricedModels);
    }

    [Fact]
    public void DeepSeekRegionalAliasUsesCurrentBaseRateWithoutStrippingOtherRoutePrefixes()
    {
        var table = PriceTable.Parse("""{"_source":"OpenRouter","_match":"exact","deepseek/deepseek-v4.1-flash":{"in":2,"out":3},"other":{"in":1}}""");
        var row = new ModelMix("PI-Desktop", "cn:deepseek-v4.1-flash", 1, 1_000_000, 0);
        Assert.Equal(2m, table.Estimate([row]).Amount);
        Assert.True(table.Estimate([row]).UsesBaseModelRate);
        Assert.Equal(1, table.Estimate([row with { Model = "cn:other" }]).UnpricedModels);
        table.Rates[row.Model] = new(3, 4, 1, 2);
        Assert.Equal(3m, table.Estimate([row]).Amount);
        Assert.False(table.Estimate([row]).UsesBaseModelRate);
    }

    [Fact]
    public void FullSkusWinAndAmbiguousVersionsOrRoutesAreNotPriced()
    {
        var items = new[]{("v/model-5.5",1),("v/model-5.5-high",2),("a/shared",1),("b/shared",2),
            ("a/choice-5-5",4),("a/choice-5.5",3),("v/gpt-5.1-codex",4),("a/model-high",1),("b/model-high",2),("a/model",3)};
        var json = JsonSerializer.Serialize(new {data=items.Select(x=>new{id=x.Item1,pricing=new{prompt=x.Item2*.000001m,completion=0}})});
        var table=PriceTable.Parse(OpenRouterPrices.Parse(json,At).Json);
        CostEstimate E(string id) => table.Estimate([new("WorkBuddy",id,1,1_000_000,0)]);
        Assert.False(E("model-5.5-high").UsesBaseModelRate);
        Assert.Equal(2m,E("model-5-5-high").Amount);
        Assert.Equal(1m,E("a/shared-medium").Amount);
        foreach(var id in new[]{"shared-medium","choice-5-5-medium","model-high","gpt-5.1-codex-max",
            "model-5.5-fast-high","other/model-5.5-high","model-5.5:batch-high","model-5.5-free-high"})
            Assert.Equal(1,E(id).UnpricedModels);
    }

    [Theory]
    [InlineData("gpt-6-astra-medium", "openai/gpt-6-astra")]
    [InlineData("gpt-6-sol-medium", "openai/gpt-6-sol")]
    [InlineData("gpt-6-luna-medium", "openai/gpt-6-luna")]
    [InlineData("gemini-4-carbon-medium", "google/gemini-4-carbon")]
    [InlineData("kimi-k3-1-pro-max", "moonshotai/kimi-k3.1-pro")]
    [InlineData("glm-5-3-max", "z-ai/glm-5.3")]
    [InlineData("claude-sonnet-5-5-medium", "anthropic/claude-sonnet-5.5")]
    [InlineData("gemini-3-8-flash-medium", "google/gemini-3.8-flash")]
    [InlineData("claude-fable-5-2-medium", "anthropic/claude-fable-5.2")]
    [InlineData("claude-fable-5-2-max", "anthropic/claude-fable-5.2")]
    public void DevinUsesCurrentCanonicalPriceWithoutRenamingOrPricingOtherSources(string id, string canonical)
    {
        var table = PriceTable.Parse("""{"_source":"OpenRouter","_match":"exact"}""");
        table.Rates[canonical] = new(2, 10, .2m, 2.5m);
        var row = new ModelMix("Devin", id, 1, 1_000_000, 100_000, 200_000, 300_000);
        var estimate = table.Estimate([row]);
        Assert.Equal(2.79m, estimate.Amount);
        Assert.True(estimate.UsesBaseModelRate);
        Assert.Equal(0, estimate.UnpricedModels);
        var mixed = table.Estimate([row, row with { Source = "API · Devin" }]);
        Assert.True(mixed.HasPricedUsage);
        Assert.Equal(1, mixed.UnpricedModels);
        Assert.Equal(estimate.Amount, mixed.Amount);
        Assert.False(table.Estimate([row with { Model = id + "-fast" }]).HasPricedUsage);
        table.Rates[id] = new(0, 0, 0, 0);
        var exact = table.Estimate([row]);
        Assert.False(exact.UsesBaseModelRate);
        Assert.True(exact.HasPricedUsage);
        Assert.Equal(0m, exact.Amount);
        table.Rates.Clear();
        Assert.Equal(1, table.Estimate([row]).UnpricedModels);
    }
}
