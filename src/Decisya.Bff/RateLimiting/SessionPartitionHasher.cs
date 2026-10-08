using System.Security.Cryptography;
using System.Text;
using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.RateLimiting;

/// <summary>
/// The <c>session</c> partition value (G2 D3, G3 T122-11): a keyed hash of the server-side ticket key,
/// never the cookie bytes and never a token. It is HMAC-SHA256 under the existing user_id key with its
/// own domain label (so it cannot match a <c>user_id</c> value), first 16 bytes as hex. The value lives
/// in the limiter's memory only; it is never logged, tagged or sent anywhere.
/// </summary>
internal sealed class SessionPartitionHasher(IOptions<DecisyaObservabilityOptions> options)
{
    private const string DomainLabel = "decisya.bff.ratelimit.session.v1\0";

    private readonly Lazy<byte[]> _key = new(() => Convert.FromBase64String(options.Value.UserIdHashKey ?? string.Empty));

    internal string Hash(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);

        var mac = HMACSHA256.HashData(_key.Value, Encoding.UTF8.GetBytes(DomainLabel + sessionKey));
        return Convert.ToHexStringLower(mac.AsSpan(0, 16));
    }
}
