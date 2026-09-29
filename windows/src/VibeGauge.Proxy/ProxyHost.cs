using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using VibeGauge.Proxy.Health;
using VibeGauge.Proxy.Security;
using VibeGauge.Proxy.Streaming;
using VibeGauge.Proxy.Usage;

namespace VibeGauge.Proxy;

public sealed class ProxyHost : IAsyncDisposable
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Host", "Content-Length", "Accept-Encoding"
    };

    private readonly ProxyOptions options;
    private readonly ProxyHealth health;
    private readonly UsageLogWriter logWriter;
    private readonly RequestValidator requestValidator;
    private readonly UpstreamClient upstream;
    private readonly StreamingForwarder streaming = new();
    private WebApplication? application;

    public ProxyHost(ProxyOptions options)
    {
        this.options = options;
        health = new(options.Port, options.UpstreamProxy, options.UpstreamError);
        logWriter = new(options.CallsPath);
        requestValidator = new(options);
        upstream = new(options);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (application is not null) return;
        Directory.CreateDirectory(options.DataDirectory);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            server.Listen(IPAddress.Loopback, options.Port, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var error = requestValidator.ValidateClient(context.Request);
            if (error is null) await next();
            else await WriteErrorAsync(context, StatusCodes.Status403Forbidden, error);
        });
        app.MapGet("/_vibegauge/health", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(health.Snapshot());
        });
        app.MapPost("/_vibegauge/shutdown", async context =>
        {
            var suppliedToken = context.Request.Headers.TryGetValue("X-VibeGauge-Control", out var value)
                ? System.Text.Encoding.UTF8.GetBytes(value.ToString())
                : [];
            var expectedToken = System.Text.Encoding.UTF8.GetBytes(options.ControlToken);
            if (expectedToken.Length == 0 || suppliedToken.Length != expectedToken.Length ||
                !CryptographicOperations.FixedTimeEquals(suppliedToken, expectedToken))
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "invalid control token");
                return;
            }
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.WriteAsJsonAsync(new { status = "stopping" });
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                app.Lifetime.StopApplication();
            });
        });
        app.MapMethods("/{**path}", ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS"], ForwardAsync);
        application = app;
        await app.StartAsync(cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        await application!.WaitForShutdownAsync(cancellationToken);
    }

    private async Task ForwardAsync(HttpContext context)
    {
        ProxyRequest request;
        try { request = await ProxyRequestParser.ParseAsync(context, options); }
        catch (ProxyRequestException error)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, error.Message);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var usage = new UsageAccumulator(request.Model);
        var status = StatusCodes.Status502BadGateway;
        var stream = request.Stream;
        var failed = false;
        var reachedUpstream = false;
        var complete = false;
        IReadOnlyDictionary<string, string>? rateLimits = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(options.UpstreamTimeout);
        try
        {
            await upstream.ValidateAsync(request.Upstream, timeout.Token);
            using var upstreamRequest = CreateUpstreamRequest(context.Request, request);
            if (upstreamRequest.Content is { } body) upstreamRequest.Content = SentContent.Create(body, () => reachedUpstream = true);
            using var response = await upstream.SendAsync(upstreamRequest, timeout.Token);
            reachedUpstream = true;
            status = (int)response.StatusCode;
            rateLimits = VibeGauge.Core.ApiQuality.SafeHeaders(response.Headers.Select(x => new KeyValuePair<string, string>(x.Key, string.Join(", ", x.Value))));
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var ndjson = contentType.Contains("ndjson", StringComparison.OrdinalIgnoreCase);
            stream = stream || ndjson || contentType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);
            context.Response.StatusCode = status;
            CopyResponseHeaders(response, context.Response, stream);
            if (stream && response.Content.Headers.ContentEncoding.Count == 0)
            {
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await streaming.ForwardAsync(source, context.Response.Body, usage, context.RequestAborted, timeout.Token, ndjson);
            }
            else
            {
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var captured = new MemoryStream();
                var buffer = new byte[64 * 1024];
                var oversized = false;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    if (captured.Length + read <= 8 * 1024 * 1024) captured.Write(buffer, 0, read);
                    else oversized = true;
                }
                if (oversized) usage.Error = "usage_body_too_large";
                else if (response.Content.Headers.ContentEncoding.Count > 0) usage.Error = "unsupported_content_encoding";
                else UsageParser.Apply(captured.GetBuffer().AsSpan(0, (int)captured.Length), usage);
            }
            complete = true;
            failed = status >= 400;
        }
        catch (HostValidationException)
        {
            status = StatusCodes.Status403Forbidden;
            failed = true;
            if (!context.Response.HasStarted) await WriteErrorAsync(context, status, "upstream address is not allowed");
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            status = StatusCodes.Status504GatewayTimeout;
            failed = true;
            if (!context.Response.HasStarted) await WriteErrorAsync(context, status, "upstream timeout");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            status = 499;
            failed = true;
        }
        catch (Exception error)
        {
            status = StatusCodes.Status502BadGateway;
            failed = true;
            if (!context.Response.HasStarted)
                await WriteErrorAsync(context, status, $"upstream request failed ({error.GetType().Name})");
        }
        finally
        {
            health.Record(usage.Parsed, failed);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var now = DateTimeOffset.Now;
            var record = new UsageRecord
            {
                Timestamp = now.ToString("O"),
                Epoch = now.ToUnixTimeMilliseconds() / 1000d,
                Host = request.Upstream.IdnHost,
                Provider = ProviderResolver.ForHost(request.Upstream.IdnHost),
                Path = SecretRedactor.PathOnly(request.Upstream),
                Model = usage.Model,
                Stream = stream,
                Status = status,
                Milliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds),
                ContextTokens = usage.ContextTokens,
                CacheReadTokens = usage.CacheReadTokens,
                CacheWriteTokens = usage.CacheWriteTokens,
                OutputTokens = usage.OutputTokens,
                ThinkingTokens = usage.ThinkingTokens,
                Parsed = usage.Parsed,
                ReachedUpstream = reachedUpstream,
                KeyFingerprint = AccountFingerprint(context.Request),
                Error = usage.Error,
                RateLimits = rateLimits,
                Complete = complete
            };
            try { await logWriter.AppendAsync(record); } catch { }
        }
    }

    private sealed class SentContent(HttpContent inner, Action sent) : HttpContent
    {
        public static SentContent Create(HttpContent inner, Action sent)
        {
            var result = new SentContent(inner, sent);
            foreach (var header in inner.Headers) result.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return result;
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { await inner.CopyToAsync(stream); sent(); }
        protected override bool TryComputeLength(out long length) { length = inner.Headers.ContentLength ?? -1; return length >= 0; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private static HttpRequestMessage CreateUpstreamRequest(HttpRequest source, ProxyRequest parsed)
    {
        var request = new HttpRequestMessage(new HttpMethod(source.Method), parsed.Upstream);
        if (parsed.Body.Length > 0 || source.Headers.Any(x => x.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)))
            request.Content = new ByteArrayContent(parsed.Body);
        foreach (var header in source.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        return request;
    }

    private static string AccountFingerprint(HttpRequest request)
    {
        var key = request.Headers.Authorization.ToString();
        if (key.Length == 0) key = request.Headers["x-api-key"].ToString();
        if (key.Length == 0) key = request.Headers["api-key"].ToString();
        return key.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target, bool stream)
    {
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            target.Headers[header.Key] = header.Value.ToArray();
        }
        target.Headers.Remove("transfer-encoding");
        if (stream) target.Headers.Remove("content-length");
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = SecretRedactor.Redact(message) }));
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            try { await application.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
            await application.DisposeAsync();
        }
        upstream.Dispose();
    }
}
