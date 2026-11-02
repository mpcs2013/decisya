#pragma warning disable CA1848, CA2254, CA1873, CA1822, CA2201, CA1810, CA1823, IDE0060, IDE0051, CA1859, CA2007
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.PersonalDataLogs;

// Issue #230 red fixture (S-03 narrowing N-01): the same shape as the green logout fixture, but the Subject (a claim
// read from the token payload) is logged. It must be reported on the sync and the async path. Synthetic values only.

/// <summary>Shaped like <c>LogoutTokenValidationResult</c>.</summary>
public sealed record LeakyLogoutOutcome(bool IsValid, string? SessionId, string? FailureReason, string? Subject = null)
{
    public static LeakyLogoutOutcome Failed(string code) => new(false, null, code);

    public static LeakyLogoutOutcome Valid(string sessionId, string? subject = null) => new(true, sessionId, null, subject);
}

/// <summary>Shaped like <c>LogoutTokenValidator</c>.</summary>
public sealed class LeakyLogoutCheck
{
    public async Task<LeakyLogoutOutcome> ValidateAsync(string logoutToken, CancellationToken ct)
    {
        JsonWebToken token;
        try
        {
            token = new JsonWebToken(logoutToken);
        }
        catch (ArgumentException)
        {
            return LeakyLogoutOutcome.Failed("malformed");
        }

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters());
        if (!validation.IsValid)
        {
            return LeakyLogoutOutcome.Failed(validation.Exception?.GetType().Name ?? "signature_or_claims");
        }

        return ReadClaims(token);
    }

    public static LeakyLogoutOutcome ReadClaims(JsonWebToken token)
    {
        token.TryGetPayloadValue<string>("sid", out var sid);
        return LeakyLogoutOutcome.Valid(sid, token.TryGetPayloadValue<string>("sub", out var subject) ? subject : null);
    }
}

/// <summary>Logs the subject claim.</summary>
public static partial class LeakyLogoutLog
{
    public static void SyncPath(ILogger logger, JsonWebToken token)
    {
        var result = LeakyLogoutCheck.ReadClaims(token);
        if (!result.IsValid || result.SessionId is null)
        {
            Rejected(logger, result.Subject ?? "unknown");
        }
    }

    public static async Task AsyncPath(ILogger logger, LeakyLogoutCheck check, string token, CancellationToken ct)
    {
        var result = await check.ValidateAsync(token, ct);
        if (!result.IsValid || result.SessionId is null)
        {
            Rejected(logger, result.Subject ?? "unknown");
        }
    }

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "Logout token rejected {Reason}")]
    private static partial void Rejected(ILogger logger, string reason);
}
