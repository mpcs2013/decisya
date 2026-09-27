using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using NodaTime;
using StackExchange.Redis;

namespace Decisya.Bff.Session;

/// <summary>
/// The only type that touches <see cref="StackExchange.Redis"/> (NetArchTest rule, G2): the
/// Redis-backed <see cref="ITicketStore"/> that holds the authentication ticket, including
/// the tokens <c>SaveTokens</c> puts on it, so the session cookie itself never carries one
/// (Story 1/2, T-01). Keeps no in-process cache (Story 3): every read goes to Redis, and a
/// Redis failure fails the caller closed rather than serving a cached claim.
/// </summary>
/// <remarks>
/// S-2 (T-17): each ticket's <see cref="IDataProtector"/> purpose is bound to its own session
/// key (<c>CreateProtector("Decisya.Bff.TicketStore.v1", key)</c>), so a ciphertext written
/// under one key can never unprotect under another — an attacker with Redis write access
/// cannot swap two users' ciphertexts.
/// </remarks>
internal sealed class RedisTicketStore(
    IConnectionMultiplexer connectionMultiplexer,
    IDataProtectionProvider dataProtectionProvider,
    IClock clock,
    ILogger<RedisTicketStore> logger) : ITicketStore
{
    private const string TicketKeyPrefix = "decisya:bff:ticket:";
    private const string SessionIdSetKeyPrefix = "decisya:bff:sid:";
    private const string ProtectorPurpose = "Decisya.Bff.TicketStore.v1";
    private const string SessionIdClaimType = "sid";
    private const int SessionKeyByteLength = 32;

    private static readonly TimeSpan RedisOperationTimeout = TimeSpan.FromMilliseconds(1500);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var key = GenerateSessionKey();
        var expiry = ComputeExpiry(ticket);
        var protectedBytes = Protect(key, ticket);
        var sid = ticket.Principal.FindFirst(SessionIdClaimType)?.Value;

        // Store: a failure propagates (G2 "fail closed"). The login callback then ends in
        // the generic ProblemDetails 500, and no cookie is issued.
        var database = connectionMultiplexer.GetDatabase();
        var transaction = database.CreateTransaction();
        _ = transaction.StringSetAsync(TicketKeyName(key), protectedBytes, expiry);
        if (sid is not null)
        {
            _ = transaction.SetAddAsync(SessionIdSetKeyName(sid), key);
            _ = transaction.KeyExpireAsync(SessionIdSetKeyName(sid), expiry);
        }

        await transaction.ExecuteAsync().ConfigureAwait(false);

        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(ticket);

        var expiry = ComputeExpiry(ticket);
        var protectedBytes = Protect(key, ticket);
        var sid = ticket.Principal.FindFirst(SessionIdClaimType)?.Value;

        // Renew: same fail-open-to-the-caller contract as Store; the caller decides what a
        // failed renewal means for the current request.
        var database = connectionMultiplexer.GetDatabase();
        var transaction = database.CreateTransaction();
        _ = transaction.StringSetAsync(TicketKeyName(key), protectedBytes, expiry);
        if (sid is not null)
        {
            _ = transaction.SetAddAsync(SessionIdSetKeyName(sid), key);
            _ = transaction.KeyExpireAsync(SessionIdSetKeyName(sid), expiry);
        }

        await transaction.ExecuteAsync().ConfigureAwait(false);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var timeoutSource = new CancellationTokenSource(RedisOperationTimeout);

        RedisValue value;
        try
        {
            var database = connectionMultiplexer.GetDatabase();
            value = await database
                .StringGetAsync(TicketKeyName(key))
                .WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransientStoreFailure(exception))
        {
            RecordFailure("retrieve", exception);
            return null;
        }

        if (value.IsNull)
        {
            return null;
        }

        var protectedBytes = (byte[]?)value;
        if (protectedBytes is null)
        {
            return null;
        }

        try
        {
            return Unprotect(key, protectedBytes);
        }
        catch (Exception exception) when (IsUnprotectFailure(exception))
        {
            // S-3 (T-07): a corrupted or non-decodable entry degrades exactly like a Redis
            // failure — never a 500, and never served from anywhere else.
            RecordFailure("retrieve", exception);
            return null;
        }
    }

    public async Task RemoveAsync(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // RetrieveAsync never throws for a transient or unprotect failure (it already logs
        // and returns null); best effort only, to learn which sid set to pull the key out of.
        // The ticket key is still deleted below either way.
        var existing = await RetrieveAsync(key).ConfigureAwait(false);
        var sid = existing?.Principal.FindFirst(SessionIdClaimType)?.Value;

        var database = connectionMultiplexer.GetDatabase();
        var transaction = database.CreateTransaction();
        _ = transaction.KeyDeleteAsync(TicketKeyName(key));
        if (sid is not null)
        {
            _ = transaction.SetRemoveAsync(SessionIdSetKeyName(sid), key);
        }

        await transaction.ExecuteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every ticket indexed under <paramref name="sid"/> (Story 7): back-channel
    /// logout's own entry point, never reached through <see cref="ITicketStore"/> itself.
    /// Idempotent: a <paramref name="sid"/> with nothing left under it deletes nothing.
    /// </summary>
    public async Task RemoveAllForSessionIdAsync(string sid)
    {
        ArgumentNullException.ThrowIfNull(sid);

        var database = connectionMultiplexer.GetDatabase();
        var sidSetKey = SessionIdSetKeyName(sid);
        var members = await database.SetMembersAsync(sidSetKey).ConfigureAwait(false);

        if (members.Length == 0)
        {
            return;
        }

        var transaction = database.CreateTransaction();
        foreach (var member in members)
        {
            var memberKey = (string?)member;
            if (memberKey is not null)
            {
                _ = transaction.KeyDeleteAsync(TicketKeyName(memberKey));
            }
        }

        _ = transaction.KeyDeleteAsync(sidSetKey);
        await transaction.ExecuteAsync().ConfigureAwait(false);
    }

    private static bool IsTransientStoreFailure(Exception exception) =>
        exception is RedisException or RedisTimeoutException or OperationCanceledException or TimeoutException;

    private static bool IsUnprotectFailure(Exception exception) =>
        exception is CryptographicException or FormatException;

    private void RecordFailure(string operation, Exception exception)
    {
        // Never the key, the ticket bytes or any claim (CLAUDE.md "never log tokens,
        // cookies, claims dictionaries"): only the operation name and the trace id the
        // exception logging scope already carries.
        BffLog.TicketStoreFailure(logger, exception, operation);
        BffTelemetry.TicketStoreFailures.Add(1, new KeyValuePair<string, object?>("operation", operation));
    }

    private byte[] Protect(string key, AuthenticationTicket ticket)
    {
        var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose, key);
        var serialized = TicketSerializer.Default.Serialize(ticket);
        return protector.Protect(serialized);
    }

    private AuthenticationTicket? Unprotect(string key, byte[] protectedBytes)
    {
        var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose, key);
        var serialized = protector.Unprotect(protectedBytes);
        return TicketSerializer.Default.Deserialize(serialized);
    }

    private TimeSpan ComputeExpiry(AuthenticationTicket ticket)
    {
        var expiresUtc = ticket.Properties.ExpiresUtc
            ?? throw new InvalidOperationException(
                "The authentication ticket carries no ExpiresUtc; the cookie handler must set one (D2, absolute expiration).");

        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        var ttl = expiresUtc - now;
        return ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(1);
    }

    private static string GenerateSessionKey() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SessionKeyByteLength));

    private static string TicketKeyName(string key) => TicketKeyPrefix + key;

    private static string SessionIdSetKeyName(string sid) => SessionIdSetKeyPrefix + sid;
}
