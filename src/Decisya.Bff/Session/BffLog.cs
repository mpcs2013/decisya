namespace Decisya.Bff.Session;

/// <summary>
/// Compiled log messages <c>Decisya.Bff</c> writes. Source-generated so the message template
/// is fixed at compile time; never takes a token, cookie, session key or claim value
/// (CLAUDE.md: "never log tokens, cookies, claims dictionaries or the client secret").
/// </summary>
internal static partial class BffLog
{
    [LoggerMessage(
        EventId = 1800,
        Level = LogLevel.Error,
        Message = "The Redis-backed ticket store failed during {Operation}.")]
    internal static partial void TicketStoreFailure(ILogger logger, Exception exception, string operation);

    [LoggerMessage(
        EventId = 1801,
        Level = LogLevel.Warning,
        Message = "A back-channel logout token was rejected: {Reason}.")]
    internal static partial void BackchannelLogoutTokenRejected(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1802,
        Level = LogLevel.Warning,
        Message = "The refresh_token grant against Keycloak's token endpoint failed.")]
    internal static partial void TokenRefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1803,
        Level = LogLevel.Information,
        Message = "Keycloak rejected the refresh token (invalid_grant); the session is ending.")]
    internal static partial void TokenRefreshInvalidGrant(ILogger logger);

    [LoggerMessage(
        EventId = 1804,
        Level = LogLevel.Warning,
        Message = "The end-session call to Keycloak at logout failed; the local sign-out still completed (G1 decision 2).")]
    internal static partial void KeycloakLogoutFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1805,
        Level = LogLevel.Warning,
        Message = "Keycloak's end-session endpoint returned a non-success status ({StatusCode}); the local sign-out still completed (G1 decision 2).")]
    internal static partial void KeycloakLogoutRejected(ILogger logger, int statusCode);

    [LoggerMessage(
        EventId = 1806,
        Level = LogLevel.Warning,
        Message = "The refresh lock could not be acquired (or its holder never finished) within the bounded wait; the caller sees 503.")]
    internal static partial void RefreshLockTimedOut(ILogger logger);

    [LoggerMessage(
        EventId = 1807,
        Level = LogLevel.Warning,
        Message = "The web root holds no index.html; the SPA shell is not served until the SPA is built and the BFF restarted.")]
    internal static partial void SpaIndexMissing(ILogger logger);
}
