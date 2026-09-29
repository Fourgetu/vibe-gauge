using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VibeGauge.Proxy.Tests;

internal sealed record CapturedRequest(string PathAndQuery, string Body, string Authorization, string ApiKey, string ContentType);

internal sealed class FakeUpstreamServer : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Task loop;

    public FakeUpstreamServer()
    {
        Port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        loop = Task.Run(RunAsync);
    }

    public int Port { get; }
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

    private async Task RunAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch when (stopping.IsCancellationRequested) { break; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var body = await new StreamReader(context.Request.InputStream, context.Request.ContentEncoding).ReadToEndAsync();
        Requests.Enqueue(new(
            context.Request.RawUrl ?? "",
            body,
            context.Request.Headers["Authorization"] ?? "",
            context.Request.Headers["x-api-key"] ?? "", context.Request.ContentType ?? ""));
        var path = context.Request.Url?.AbsolutePath ?? "/";
        try
        {
            if (path == "/openai-json")
            {
                context.Response.Headers.Add("x-ratelimit-remaining-tokens", "999");
                context.Response.Headers.Add("x-ratelimit-reset-requests", "1m30s");
                context.Response.Headers.Add("x-provider-secret", "RESPONSE_SECRET");
                await JsonAsync(context, """
                    {"id":"chat","model":"deepseek-chat","choices":[{"message":{"content":"你好"}}],"usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120,"prompt_tokens_details":{"cached_tokens":60},"completion_tokens_details":{"reasoning_tokens":4}}}
                    """);
            }
            else if (path == "/openai-responses")
            {
                await JsonAsync(context, """
                    {"id":"resp","model":"gpt-test","output":[],"usage":{"input_tokens":44,"output_tokens":9,"input_tokens_details":{"cached_tokens":11},"output_tokens_details":{"reasoning_tokens":3}}}
                    """);
            }
            else if (path == "/openai-sse")
            {
                await SseAsync(context,
                    """data: {"id":"a","model":"openrouter/test","choices":[{"delta":{"content":"A"}}],"usage":null}\n\n""",
                    """data: {"id":"b","model":"openrouter/test","choices":[],"usage":{"prompt_tokens":80,"completion_tokens":12,"prompt_tokens_details":{"cached_tokens":50},"completion_tokens_details":{"reasoning_tokens":2}}}\n\n""",
                    "data: [DONE]\n\n");
            }
            else if (path == "/anthropic-json")
            {
                await JsonAsync(context, """
                    {"type":"message","model":"glm-4.7","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":2,"output_tokens":7}}
                    """);
            }
            else if (path == "/anthropic-sse")
            {
                await SseAsync(context,
                    """event: message_start\ndata: {"type":"message_start","message":{"model":"claude-test","usage":{"input_tokens":12,"cache_read_input_tokens":6,"cache_creation_input_tokens":3,"output_tokens":0}}}\n\n""",
                    """event: message_delta\ndata: {"type":"message_delta","usage":{"output_tokens":8}}\n\n""",
                    """event: message_stop\ndata: {"type":"message_stop"}\n\n""");
            }
            else if (path.StartsWith("/status/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = int.Parse(path["/status/".Length..]);
                await JsonAsync(context, """{"error":"fixture"}""", preserveStatus: true);
            }
            else if (path == "/timeout")
            {
                await Task.Delay(1500);
                await JsonAsync(context, """{"late":true}""");
            }
            else if (path == "/invalid")
            {
                await BytesAsync(context, "application/json", Encoding.UTF8.GetBytes("{not-json"));
            }
            else if (path == "/no-usage")
            {
                await JsonAsync(context, """{"model":"unknown","choices":[]}""");
            }
            else if (path == "/stream-break")
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentLength64 = 1024;
                var partial = Encoding.UTF8.GetBytes("""data: {"model":"broken","usage":""");
                await context.Response.OutputStream.WriteAsync(partial);
                await context.Response.OutputStream.FlushAsync();
                context.Response.Abort();
            }
            else if (path is "/echo" or "/large")
            {
                await JsonAsync(context, """
                    {"model":"fixture-model","usage":{"prompt_tokens":3,"completion_tokens":2}}
                    """);
            }
            else
            {
                context.Response.StatusCode = 404;
                await JsonAsync(context, """{"error":"not found"}""", preserveStatus: true);
            }
        }
        catch { try { context.Response.Abort(); } catch { } }
    }

    private static async Task JsonAsync(HttpListenerContext context, string json, bool preserveStatus = false)
    {
        if (!preserveStatus) context.Response.StatusCode = 200;
        await BytesAsync(context, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
    }

    private static async Task BytesAsync(HttpListenerContext context, string contentType, byte[] body)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static async Task SseAsync(HttpListenerContext context, params string[] chunks)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        foreach (var chunk in chunks)
        {
            var bytes = Encoding.UTF8.GetBytes(chunk.Replace("\\n", "\n", StringComparison.Ordinal));
            await context.Response.OutputStream.WriteAsync(bytes);
            await context.Response.OutputStream.FlushAsync();
            await Task.Delay(35);
        }
        context.Response.Close();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();
        listener.Close();
        try { await loop; } catch { }
        stopping.Dispose();
    }
}
