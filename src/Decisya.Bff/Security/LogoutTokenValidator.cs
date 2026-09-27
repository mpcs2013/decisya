using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Decisya.Bff.Security;

/// <summary>The outcome of validating a back-channel logout token (Story 7, G4-18-03).
/// <see cref="FailureReason"/> is a fixed, non-parameterised code — never response-visible
/// (S-4: "log the rejection reason server-side only") — safe to log because it names which
/// rule failed, never a claim or token value.</summary>
internal sealed record LogoutTokenValidationResult(bool IsValid, string? SessionId, string? FailureReason)
{
    internal static LogoutTokenValidationResult Invalid(string reason) => new(false, null, reason);

    internal static LogoutTokenValidationResult Valid(string sessionId) => new(true, sessionId, null);
}

/// <summary>
/// Validates a back-channel logout token against every rule G2 and G3 name (T-10): JWKS
/// signature, <c>iss</c>, <c>aud</c>, RS256/ES256 only, <c>events</c> carries the
/// back-channel-logout event URI, <c>sid</c> present, <c>nonce</c> absent, and <c>iat</c>
/// within 60 s of clock skew. Uses the OIDC handler's own <c>ConfigurationManager</c> (same
/// discovery document and JWKS) — no separate signing-key fetch.
/// </summary>
internal sealed class LogoutTokenValidator(IOptionsMonitor<OpenIdConnectOptions> oidcOptionsMonitor, IClock clock)
{
    private const string BackchannelLogoutEventUri = "http://schemas.openid.net/event/backchannel-logout";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<LogoutTokenValidationResult> ValidateAsync(string logoutToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(logoutToken);

        var oidcOptions = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configurationManager = oidcOptions.ConfigurationManager
            ?? throw new InvalidOperationException("The OIDC handler has no ConfigurationManager.");
        var configuration = await configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidateIssuer = true,
            ValidAudience = oidcOptions.ClientId,
            ValidateAudience = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = ["RS256", "ES256"],
            RequireSignedTokens = true,
            RequireExpirationTime = false,
            // The framework's lifetime validator treats a token with no "exp" claim as
            // having Expires == DateTime.MinValue, not null, so RequireExpirationTime=false
            // alone does not skip it (it reports the token as already expired). "exp" is
            // validated manually below instead, exactly the same way as "iat".
            ValidateLifetime = false,
            ClockSkew = ClockSkew,
        };

        JsonWebToken token;
        try
        {
            token = new JsonWebToken(logoutToken);
        }
        catch (ArgumentException)
        {
            return LogoutTokenValidationResult.Invalid("malformed");
        }

        var validationResult = await Handler.ValidateTokenAsync(token, validationParameters).ConfigureAwait(false);
        if (!validationResult.IsValid)
        {
            // The exception type alone (never the message, which can echo the token's own
            // iss/aud values): e.g. SecurityTokenInvalidSignatureException, SecurityTokenInvalidIssuerException.
            return LogoutTokenValidationResult.Invalid(validationResult.Exception?.GetType().Name ?? "signature_or_claims");
        }

        return ValidateLogoutSpecificClaims(token);
    }

    private LogoutTokenValidationResult ValidateLogoutSpecificClaims(JsonWebToken token)
    {
        // "events" must carry the back-channel-logout event URI (as a key of a JSON object).
        // The concrete Dictionary<string, object> — not the IDictionary<string, object>
        // interface — is required: System.Text.Json cannot deserialize to the interface here.
        if (!token.TryGetPayloadValue<Dictionary<string, object>>("events", out var events)
            || !events.ContainsKey(BackchannelLogoutEventUri))
        {
            return LogoutTokenValidationResult.Invalid("events");
        }

        if (!token.TryGetPayloadValue<string>("sid", out var sid) || string.IsNullOrEmpty(sid))
        {
            return LogoutTokenValidationResult.Invalid("sid");
        }

        // A logout token must never carry "nonce" (that would be an ID-token-shaped token).
        if (token.TryGetPayloadValue<string>("nonce", out _))
        {
            return LogoutTokenValidationResult.Invalid("nonce");
        }

        if (!token.TryGetPayloadValue<long>("iat", out var issuedAtUnixSeconds))
        {
            return LogoutTokenValidationResult.Invalid("iat_missing");
        }

        var nowUnixSeconds = clock.GetCurrentInstant().ToUnixTimeSeconds();
        if (issuedAtUnixSeconds > nowUnixSeconds + ClockSkew.TotalSeconds)
        {
            return LogoutTokenValidationResult.Invalid("iat_future");
        }

        // "exp" is optional on a logout token, but when present it must not be in the past.
        if (token.TryGetPayloadValue<long>("exp", out var expiresAtUnixSeconds)
            && expiresAtUnixSeconds < nowUnixSeconds - ClockSkew.TotalSeconds)
        {
            return LogoutTokenValidationResult.Invalid("exp_past");
        }

        return LogoutTokenValidationResult.Valid(sid);
    }
}
