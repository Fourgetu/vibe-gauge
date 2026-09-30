using System.Net.Http;
using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public static class OpenRouterPriceUpdater
{
    public static async Task<OpenRouterPriceSnapshot> UpdateAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        using var response = await client.GetAsync("https://openrouter.ai/api/v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();
        var snapshot = OpenRouterPrices.Parse(await response.Content.ReadAsStringAsync(cancellationToken), DateTimeOffset.Now);
        OpenRouterPrices.Save(paths, snapshot);
        return snapshot;
    }
}
