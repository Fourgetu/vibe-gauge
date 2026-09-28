using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace VibeGauge.Proxy;

public sealed record ProxyRequest(Uri Upstream, byte[] Body, string? Model, bool Stream);

public static class ProxyRequestParser
{
    public static async Task<ProxyRequest> ParseAsync(HttpContext context, ProxyOptions options)
    {
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
        if (string.IsNullOrWhiteSpace(rawTarget) || rawTarget[0] != '/' ||
            !Uri.TryCreate(rawTarget[1..], UriKind.Absolute, out var upstream))
            throw new ProxyRequestException("path must contain an absolute upstream URL");
        if (upstream.Scheme is not ("http" or "https"))
            throw new ProxyRequestException("only http and https upstream URLs are allowed");
        if (string.IsNullOrWhiteSpace(upstream.Host))
            throw new ProxyRequestException("upstream host is required");

        var body = await ReadBodyAsync(context.Request, options.MaxRequestBodyBytes, context.RequestAborted);
        string? model = null;
        var stream = false;
        if (body.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String)
                        model = modelValue.GetString();
                    if (root.TryGetProperty("stream", out var streamValue) &&
                        streamValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        stream = streamValue.GetBoolean();
                }
            }
            catch (JsonException) { }
        }
        return new(upstream, body, model, stream);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, long maximum, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maximum)
            throw new ProxyRequestException($"request body exceeds {maximum} bytes");
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new ProxyRequestException($"request body exceeds {maximum} bytes");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }
}

public sealed class ProxyRequestException(string message) : Exception(message);
