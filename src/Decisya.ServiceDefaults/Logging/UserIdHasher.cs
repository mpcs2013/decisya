using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Turns a raw user id into the keyed, one-way <c>user_id</c> value that logs carry.
/// </summary>
/// <remarks>
/// <para>
/// An unkeyed digest would be reversible for known ids: an attacker holding the logs
/// hashes a dictionary of emails, usernames or subject ids and matches them. The hash is
/// therefore HMAC-SHA256 keyed with
/// <see cref="DecisyaObservabilityOptions.UserIdHashKey"/>.
/// </para>
/// <para>
/// The input is prefixed with a fixed domain label so the same key cannot be repurposed
/// to produce a matching digest in another context. The output is the first 16 bytes of
/// the MAC as 32 lowercase hex characters: enough to correlate a user across log lines,
/// short enough to read.
/// </para>
/// </remarks>
internal sealed class UserIdHasher
{
    private const string DomainLabel = "decisya.user_id.v1\0";

    private readonly Lazy<byte[]> _key;

    public UserIdHasher(IOptions<DecisyaObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _key = new Lazy<byte[]>(() => DecodeKey(options.Value.UserIdHashKey));
    }

    /// <summary>Test seam: builds a hasher over an already-decoded key.</summary>
    internal UserIdHasher(byte[] key) => _key = new Lazy<byte[]>(key);

    /// <summary>
    /// The keyed hash of <paramref name="userId"/>, or <see langword="null"/> when there
    /// is no user id. An empty id is never hashed: a constant digest would look like a
    /// real user in the logs.
    /// </summary>
    public string? Hash(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        var payload = Encoding.UTF8.GetBytes(DomainLabel + userId);
        var mac = HMACSHA256.HashData(_key.Value, payload);

        return Convert.ToHexStringLower(mac.AsSpan(0, 16));
    }

    private static byte[] DecodeKey(string? configured)
    {
        if (!DecisyaObservabilityOptionsValidator.IsAcceptableKey(configured))
        {
            // Reached only if options validation was bypassed. The message names the
            // setting, never the value.
            throw new InvalidOperationException(DecisyaObservabilityOptionsValidator.InvalidKeyMessage);
        }

        return Convert.FromBase64String(configured!);
    }
}
