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
}
