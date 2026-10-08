using System.Net;
using System.Net.Sockets;

namespace Decisya.Bff.RateLimiting;

/// <summary>
/// The configured trusted proxies (<c>Decisya:Edge:TrustedProxies</c>, #120), read once from
/// <see cref="IConfiguration"/> on first use. Used for one thing only: an address equal to a trusted
/// proxy means forwarding did not apply (G3 G4-122-01 a), so the request is counted as
/// <c>unknown</c> instead of silently sharing the proxy's own bucket. An entry that is not a single
/// address is ignored here: <c>UseDecisyaForwardedHeaders</c> already fails start-up on it.
/// </summary>
internal sealed class TrustedProxies(IConfiguration configuration)
{
    private const string Key = "Decisya:Edge:TrustedProxies";

    private readonly Lazy<IReadOnlyCollection<IPAddress>> _addresses = new(() => Read(configuration));

    internal IReadOnlyCollection<IPAddress> Addresses => _addresses.Value;

    private static List<IPAddress> Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(Key);
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            raw.AddRange(section.Value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                raw.Add(child.Value.Trim());
            }
        }

        var result = new List<IPAddress>(raw.Count);
        foreach (var entry in raw)
        {
            if (!entry.Contains('/', StringComparison.Ordinal) && IPAddress.TryParse(entry, out var address))
            {
                result.Add(ClientPartition.Normalize(address));
            }
        }

        return result;
    }
}

/// <summary>
/// The <c>ip</c> and <c>unknown</c> partition values (G2 D3, G3 G4-122-01 a). The input is only
/// <c>HttpContext.Connection.RemoteIpAddress</c>, which the forwarded-headers middleware has already
/// replaced from <c>X-Forwarded-For</c> when (and only when) the peer is the trusted edge. This code
/// reads no request header.
/// </summary>
internal static class ClientPartition
{
    internal static (PartitionKind Kind, string Value) FromAddress(IPAddress? address, IReadOnlyCollection<IPAddress> trustedProxies)
    {
        ArgumentNullException.ThrowIfNull(trustedProxies);

        if (address is null)
        {
            return Unknown();
        }

        var normalized = Normalize(address);
        if (normalized.Equals(IPAddress.Any) || normalized.Equals(IPAddress.IPv6Any))
        {
            return Unknown();
        }

        foreach (var proxy in trustedProxies)
        {
            if (normalized.Equals(proxy))
            {
                // The peer is the edge itself: no client address was forwarded (or forwarding is off).
                // One shared, visible bucket, never a bucket per proxy that looks like a client.
                return Unknown();
            }
        }

        if (normalized.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // A client controls its whole /64: group by prefix so rotating inside it gains nothing.
            var bytes = normalized.GetAddressBytes();
            return (PartitionKind.Ip, "v6:" + Convert.ToHexStringLower(bytes.AsSpan(0, 8)));
        }

        return (PartitionKind.Ip, "v4:" + normalized.ToString());
    }

    /// <summary>IPv4-mapped IPv6 becomes IPv4; an IPv6 scope id is dropped.</summary>
    internal static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }

    private static (PartitionKind Kind, string Value) Unknown() => (PartitionKind.Unknown, "-");
}
