using Microsoft.Extensions.Configuration;

namespace Decisya.ServiceDefaults.Production;

/// <summary>
/// The management listener (issue #120, T120-02): outside Development the health endpoints
/// answer on this port only, and only requests that <b>arrived on this local port</b> are
/// treated as management traffic. The <c>Host</c> header is never consulted: Caddy passes the
/// client's <c>Host</c> through unchanged, so a header check would let a LAN client reach
/// the health checks through the edge.
/// </summary>
internal static class ManagementPort
{
    /// <summary>The fixed production port. Caddy never routes it and Compose never publishes it.</summary>
    internal const int Default = 8081;

    /// <summary>
    /// Test seam only. Production never sets it, and the deploy guard asserts the key is
    /// absent from the generated Compose file.
    /// </summary>
    internal const string ConfigurationKey = "Decisya:Management:Port";

    internal static int Resolve(IConfiguration configuration)
    {
        var raw = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Default;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be an integer from 1 to 65535.");
        }

        return port;
    }
}
