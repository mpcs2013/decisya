using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Mints tokens for harness A (G2): an RSA-2048 key the tests configure the handler to trust
/// (<see cref="RsaSigningKey"/>), an EC P-256 key for the positive ES256 row
/// (<see cref="EcSigningKey"/>), a second RSA key the handler never sees
/// (<see cref="OtherRsaSigningKey"/>, the unknown-<c>kid</c>/wrong-key rows) and an "attacker"
/// RSA key for the embedded-<c>jwk</c>-header row. Genuine tokens go through
/// <see cref="JsonWebTokenHandler"/>; <c>alg=none</c> and HS256 tokens are built by hand
/// (G2), because neither is a shape <see cref="JsonWebTokenHandler"/> will produce.
/// </summary>
public sealed class TestTokenIssuer : IDisposable
{
    internal const string Issuer = "https://issuer.test/realms/decisya";
    internal const string Audience = Decisya.Api.Authentication.ApiJwtDefaults.Audience;
    internal const string DevAliceTenantId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    internal const string SigningKeyId = "test-rsa-key";
    internal const string EcSigningKeyId = "test-ec-key";
    internal const string OtherKeyId = "other-rsa-key";

    // SetDefaultTimesOnTokenCreation defaults to true, which would silently inject an "exp" (and
    // "iat"/"nbf") claim into a token this issuer means to mint without one (the "no exp claim"
    // row) — found empirically: G4-20-01's row was passing (200) instead of failing (401) until
    // this was set to false.
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    private readonly RSA _rsaKey = RSA.Create(2048);
    private readonly RSA _otherRsaKey = RSA.Create(2048);
    private readonly RSA _attackerRsaKey = RSA.Create(2048);
    private readonly ECDsa _ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The one key the tests configure the handler's static configuration manager to trust.</summary>
    internal RsaSecurityKey RsaSigningKey => new(_rsaKey) { KeyId = SigningKeyId };

    internal ECDsaSecurityKey EcSigningKey => new(_ecKey) { KeyId = EcSigningKeyId };

    /// <summary>A genuine RSA key the handler's configuration never lists: the "unknown kid /
    /// signed by a second key" row (T-01).</summary>
    internal RsaSecurityKey OtherRsaSigningKey => new(_otherRsaKey) { KeyId = OtherKeyId };

    public void Dispose()
    {
        _rsaKey.Dispose();
        _otherRsaKey.Dispose();
        _attackerRsaKey.Dispose();
        _ecKey.Dispose();
    }

    /// <summary>The realm signing key's public component, PEM-encoded (SubjectPublicKeyInfo) —
    /// one of the two algorithm-confusion HMAC-secret shapes (G3).</summary>
    internal string RsaPublicKeyPem()
    {
        var der = _rsaKey.ExportSubjectPublicKeyInfo();
        var base64 = Convert.ToBase64String(der);
        var builder = new StringBuilder("-----BEGIN PUBLIC KEY-----\n");
        for (var i = 0; i < base64.Length; i += 64)
        {
            builder.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        }

        builder.Append("-----END PUBLIC KEY-----\n");
        return builder.ToString();
    }

    /// <summary>The realm signing key's public component, raw DER bytes — the other
    /// algorithm-confusion HMAC-secret shape (G3).</summary>
    internal byte[] RsaPublicKeyDer() => _rsaKey.ExportSubjectPublicKeyInfo();

    /// <summary>BannedSymbols.txt bans <c>DateTimeOffset.UtcNow</c> repo-wide (CLAUDE.md
    /// platform invariant 4); reads "now" through NodaTime's <see cref="NodaTime.SystemClock"/>
    /// instead. A fixed <see cref="NodaTime.IClock"/> is unnecessary here: these tokens are
    /// consumed within milliseconds of minting, never stored.</summary>
    internal static DateTimeOffset Now() => NodaTime.SystemClock.Instance.GetCurrentInstant().ToDateTimeOffset();

    /// <summary>Default, otherwise-genuine claim set (G1 Story 2's scenario outline preamble):
    /// callers override only what a row needs to vary.</summary>
    internal static Dictionary<string, object> DefaultClaims(
        string? subject = "dev-alice",
        string? tenantId = DevAliceTenantId,
        string? tokenType = "Bearer",
        string? issuer = Issuer,
        string? audience = Audience,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null,
        bool includeExpiry = true)
    {
        // IdentityModel's lifetime validator rejects a token whose "iat" is after its "exp" as an
        // internally-inconsistent lifetime (IDX10224), the same code path a bogus nbf-after-exp
        // uses — found empirically, running the NFR-27 boundary row in isolation. A token
        // simulating a past expiry (an explicit `expires` in the past) must therefore carry an
        // "iat" safely before that `expires`, exactly as a genuine 300 s-lifespan Keycloak token
        // would, rather than defaulting "iat" to "now" (which is *after* a past `expires`).
        var effectiveExpires = expires ?? Now().AddMinutes(5);
        var now = issuedAt ?? (expires is not null ? effectiveExpires.AddMinutes(-5) : Now());
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iat"] = now.ToUnixTimeSeconds(),
        };

        if (issuer is not null)
        {
            claims["iss"] = issuer;
        }

        if (audience is not null)
        {
            claims["aud"] = audience;
        }

        if (includeExpiry)
        {
            claims["exp"] = effectiveExpires.ToUnixTimeSeconds();
        }

        if (notBefore is not null)
        {
            claims["nbf"] = notBefore.Value.ToUnixTimeSeconds();
        }

        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (tenantId is not null)
        {
            claims["tenant_id"] = tenantId;
        }

        if (tokenType is not null)
        {
            claims["typ"] = tokenType;
        }

        return claims;
    }

    /// <summary>A genuine, correctly signed token under an arbitrary algorithm/key combination.</summary>
    internal static string IssueToken(
        IDictionary<string, object> claims,
        SecurityKey signingKey,
        string algorithm,
        IDictionary<string, object>? additionalHeaderClaims = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>(claims, StringComparer.Ordinal),
            SigningCredentials = new SigningCredentials(signingKey, algorithm),
            AdditionalHeaderClaims = additionalHeaderClaims,
        };

        return Handler.CreateToken(descriptor);
    }

    /// <summary>A convenience wrapper: a genuine, otherwise-valid RS256 access token.</summary>
    internal string IssueValidAccessToken(
        string? subject = "dev-alice", string? tenantId = DevAliceTenantId, string? tokenType = "Bearer") =>
        IssueToken(DefaultClaims(subject, tenantId, tokenType), RsaSigningKey, SecurityAlgorithms.RsaSha256);

    /// <summary>Embeds an attacker-controlled <c>jwk</c> header alongside a signature from that
    /// same attacker key (G3): proves the handler never honours a self-declared key.</summary>
    internal string IssueTokenWithEmbeddedAttackerJwk(IDictionary<string, object> claims)
    {
        var parameters = _attackerRsaKey.ExportParameters(includePrivateParameters: false);
        var jwk = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["kty"] = "RSA",
            ["n"] = Base64Url.EncodeToString(parameters.Modulus!),
            ["e"] = Base64Url.EncodeToString(parameters.Exponent!),
        };

        return IssueToken(
            claims,
            new RsaSecurityKey(_attackerRsaKey) { KeyId = SigningKeyId },
            SecurityAlgorithms.RsaSha256,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["jwk"] = jwk });
    }

    /// <summary>Builds an <c>alg=none</c> token by hand: no library ever produces one.</summary>
    internal static string BuildAlgNoneToken(
        IDictionary<string, object> claims, string algSpelling = "none", bool includeGarbageSignature = false)
    {
        object header = new Dictionary<string, object>(StringComparer.Ordinal) { ["alg"] = algSpelling, ["typ"] = "JWT" };
        var headerSegment = Base64UrlEncode(header);
        var payloadSegment = Base64UrlEncode(claims);
        var signatureSegment = includeGarbageSignature
            ? Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))
            : string.Empty;

        return $"{headerSegment}.{payloadSegment}.{signatureSegment}";
    }

    /// <summary>Builds an HS256 token by hand, keyed with an arbitrary secret (a random secret,
    /// or the realm's own RSA public key bytes — the algorithm-confusion attack, G3).</summary>
    internal static string BuildHs256Token(byte[] secret, IDictionary<string, object> claims, string? keyId = null)
    {
        var header = new Dictionary<string, object>(StringComparer.Ordinal) { ["alg"] = "HS256", ["typ"] = "JWT" };
        if (keyId is not null)
        {
            header["kid"] = keyId;
        }

        var headerSegment = Base64UrlEncode(header);
        var payloadSegment = Base64UrlEncode(claims);
        var signingInput = Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");

        using var hmac = new HMACSHA256(secret);
        var signature = hmac.ComputeHash(signingInput);

        return $"{headerSegment}.{payloadSegment}.{Base64Url.EncodeToString(signature)}";
    }

    private static string Base64UrlEncode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));
}
