using System.Net;
using System.Net.Sockets;

namespace VibeGauge.Proxy.Security;

public sealed class HostValidator(bool allowLoopback)
{
    public async Task<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(IsAllowed).ToArray();
        if (allowed.Length == 0) throw new HostValidationException("upstream address is not allowed");
        return allowed;
    }

    public bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsAllowed(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return allowLoopback;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254) return false;
            if (bytes[0] == 10 || bytes[0] == 127) return false;
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return false;
            if (bytes[0] == 192 && bytes[1] == 168) return false;
            if (bytes[0] == 0) return false;
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return false;
            return true;
        }
        return false;
    }
}

public sealed class HostValidationException(string message) : Exception(message);
