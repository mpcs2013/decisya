using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Decisya.Bff.Tests;

/// <summary>The RSA key the fixture's static <c>OpenIdConnectConfiguration</c> publishes as
/// its only signing key, plus the issuer/audience every valid test logout token uses.</summary>
public sealed class TestSigningContext : IDisposable
{
    public required System.Security.Cryptography.RSA Rsa { get; init; }

    public required RsaSecurityKey SigningKey { get; init; }

    public required string Issuer { get; init; }

    public required string Audience { get; init; }

    public void Dispose() => Rsa.Dispose();
}

/// <summary>
/// Builds back-channel logout tokens for G4-18-03's red tests: a test RSA key against a
/// static configuration, per G2's test harness note ("no Keycloak-to-host network path is
/// needed"). Every parameter defaults to a valid token; each test overrides exactly one.
/// </summary>
internal static class LogoutTokenBuilder
{
    private const string BackchannelLogoutEventUri = "http://schemas.openid.net/event/backchannel-logout";

    internal static TestSigningContext CreateContext(string issuer, string audience)
    {
        var rsa = System.Security.Cryptography.RSA.Create(2048);
        return new TestSigningContext
        {
            Rsa = rsa,
            SigningKey = new RsaSecurityKey(rsa) { KeyId = "decisya-bff-tests-key-1" },
            Issuer = issuer,
            Audience = audience,
        };
    }

    internal static string Build(
        TestSigningContext context,
        string? sid = "default-sid",
        bool includeEvents = true,
        bool includeNonce = false,
        string? issuer = null,
        string? audience = null,
        SecurityKey? signingKey = null,
        string? algorithm = SecurityAlgorithms.RsaSha256,
        long? issuedAtUnixSeconds = null,
        long? expiresAtUnixSeconds = null,
        string? subject = null)
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };

        var claims = new Dictionary<string, object>
        {
            ["iat"] = issuedAtUnixSeconds ?? SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds(),
        };

        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (sid is not null)
        {
            claims["sid"] = sid;
        }

        if (includeEvents)
        {
            claims["events"] = new Dictionary<string, object> { [BackchannelLogoutEventUri] = new Dictionary<string, object>() };
        }

        if (includeNonce)
        {
            claims["nonce"] = Guid.NewGuid().ToString("N");
        }

        if (expiresAtUnixSeconds is not null)
        {
            claims["exp"] = expiresAtUnixSeconds.Value;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? context.Issuer,
            Audience = audience ?? context.Audience,
            Claims = claims,
        };

        if (!string.Equals(algorithm, "none", StringComparison.Ordinal))
        {
            var effectiveKey = signingKey ?? context.SigningKey;
            descriptor.SigningCredentials = new SigningCredentials(effectiveKey, algorithm ?? SecurityAlgorithms.RsaSha256);
        }

        return handler.CreateToken(descriptor);
    }

    /// <summary>An RSA key deliberately absent from <see cref="TestSigningContext.SigningKey"/>'s
    /// configuration, for the "wrong signing key" case.</summary>
    internal static RsaSecurityKey CreateUnrelatedSigningKey() =>
        new(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "unrelated-key" };

    /// <summary>An HMAC key built from the context's own RSA public key bytes, for the
    /// "HS256 signed with the RSA public key's bytes" case.</summary>
    internal static SymmetricSecurityKey CreateHmacKeyFromRsaPublicKey(TestSigningContext context) =>
        new(context.Rsa.ExportRSAPublicKey());
}
