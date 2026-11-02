#pragma warning disable CA1848, CA2254, CA1873, CA1822, IDE0060, CA1859, CA2007
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.PersonalDataLogs;

// Issue #230 green fixture (S-03 narrowing N-01): a result record built by Valid (claims from the token payload) and by
// Failed (a fixed code). Logging only FailureReason must stay clean on the sync and the async path. Synthetic values only.

/// <summary>Shaped like <c>LogoutTokenValidationResult</c>.</summary>
public sealed record LogoutOutcome(bool IsValid, string? SessionId, string? FailureReason, string? Subject = null)
{
    public static LogoutOutcome Failed(string code) => new(false, null, code);

    public static LogoutOutcome Valid(string sessionId, string? subject = null) => new(true, sessionId, null, subject);
}

/// <summary>Shaped like <c>LogoutTokenValidator</c>.</summary>
public sealed class LogoutTokenCheck
{
    public async Task<LogoutOutcome> ValidateAsync(string logoutToken, CancellationToken ct)
    {
        JsonWebToken token;
        try
        {
            token = new JsonWebToken(logoutToken);
        }
        catch (ArgumentException)
        {
            return LogoutOutcome.Failed("malformed");
        }

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters());
        if (!validation.IsValid)
        {
            return LogoutOutcome.Failed(validation.Exception?.GetType().Name ?? "signature_or_claims");
        }

        return ReadClaims(token);
    }

    public static LogoutOutcome ReadClaims(JsonWebToken token)
    {
        token.TryGetPayloadValue<string>("sid", out var sid);
        return LogoutOutcome.Valid(sid, token.TryGetPayloadValue<string>("sub", out var subject) ? subject : null);
    }
}

/// <summary>Logs only the fixed failure code.</summary>
public static partial class LogoutRejectionLog
{
    public static void SyncPath(ILogger logger, JsonWebToken token)
    {
        var result = LogoutTokenCheck.ReadClaims(token);
        if (!result.IsValid || result.SessionId is null)
        {
            Rejected(logger, result.FailureReason ?? "unknown");
        }
    }

    public static async Task AsyncPath(ILogger logger, LogoutTokenCheck check, string token, CancellationToken ct)
    {
        var result = await check.ValidateAsync(token, ct);
        if (!result.IsValid || result.SessionId is null)
        {
            Rejected(logger, result.FailureReason ?? "unknown");
        }
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Logout token rejected {Reason}")]
    private static partial void Rejected(ILogger logger, string reason);
}
