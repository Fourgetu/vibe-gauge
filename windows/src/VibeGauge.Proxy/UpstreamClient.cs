using System.Net;
using System.Net.Sockets;
using VibeGauge.Proxy.Security;

namespace VibeGauge.Proxy;

public sealed class UpstreamClient : IDisposable
{
    private readonly HostValidator validator;
    private readonly HttpClient client;
    private readonly IWebProxy? route;

    public UpstreamClient(ProxyOptions options)
    {
        validator = new(options.AllowLoopbackUpstream);
        route = options.UpstreamProxy == "direct" ? null : string.IsNullOrWhiteSpace(options.UpstreamProxy)
            ? HttpClient.DefaultProxy : new ExplicitProxy(options.UpstreamProxy, options.NoProxy);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = route is not null,
            Proxy = route,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync
        };
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public Task ValidateAsync(Uri upstream, CancellationToken cancellationToken) =>
        validator.ResolveAllowedAsync(upstream.IdnHost, cancellationToken);

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var requestUri = context.InitialRequestMessage.RequestUri!;
        var configuredProxy = route is not null && !route.IsBypassed(requestUri) ? route.GetProxy(requestUri) : null;
        var connectingProxy = configuredProxy is not null && configuredProxy != requestUri &&
            configuredProxy.IdnHost.Equals(context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase) &&
            configuredProxy.Port == context.DnsEndPoint.Port;
        // Private addresses are allowed only for the user's configured proxy, never the request destination.
        var addresses = connectingProxy ? await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            : await validator.ResolveAllowedAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error)
            {
                last = error;
                socket.Dispose();
            }
        }
        throw new HttpRequestException("unable to connect to an allowed upstream address", last);
    }

    public void Dispose() => client.Dispose();

    private sealed class ExplicitProxy : IWebProxy
    {
        private readonly Uri proxy;
        private readonly string[] bypass;
        public ExplicitProxy(string address, string[] bypass)
        {
            var uri = new Uri(address);
            if (uri.UserInfo.Length > 0)
            {
                var pair = uri.UserInfo.Split(':', 2);
                Credentials = new NetworkCredential(Uri.UnescapeDataString(pair[0]), pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");
                uri = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
            }
            proxy = uri;
            this.bypass = bypass;
        }
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => IsBypassed(destination) ? destination : proxy;
        public bool IsBypassed(Uri host) => host.IsLoopback || bypass.Any(x =>
            host.IdnHost.Equals(x.TrimStart('.'), StringComparison.OrdinalIgnoreCase) ||
            host.IdnHost.EndsWith("." + x.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }
}
