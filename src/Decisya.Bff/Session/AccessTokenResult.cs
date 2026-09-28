namespace Decisya.Bff.Session;

internal enum AccessTokenStatus
{
    /// <summary>A usable access token, fresh or freshly refreshed.</summary>
    Token,

    /// <summary>Keycloak rejected the refresh (B-1), or the ticket is simply gone; the caller
    /// must sign the cookie scheme out and return 401. Nothing is forwarded.</summary>
    SessionEnded,

    /// <summary>A transient failure (D6): Keycloak unreachable, a non-<c>invalid_grant</c>
    /// error, or the refresh lock could not be acquired in time. The session is kept; the
    /// caller returns a generic 503. Nothing is forwarded.</summary>
    Unavailable,
}

/// <summary>The result of <see cref="AccessTokenProvider.GetAsync"/>. Never logged or exposed
/// to the client whole; only <see cref="AccessToken"/>'s value is attached to the forwarded
/// request's <c>Authorization</c> header.</summary>
internal sealed record AccessTokenResult(AccessTokenStatus Status, string? AccessToken)
{
    internal static AccessTokenResult Token(string accessToken) => new(AccessTokenStatus.Token, accessToken);

    internal static readonly AccessTokenResult SessionEnded = new(AccessTokenStatus.SessionEnded, null);

    internal static readonly AccessTokenResult Unavailable = new(AccessTokenStatus.Unavailable, null);
}
