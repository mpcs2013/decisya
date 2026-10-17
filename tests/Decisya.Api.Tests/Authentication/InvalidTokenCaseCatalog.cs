using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Story 2's scenario outline rows, plus G3's additions (the algorithm-confusion PEM/DER
/// rows, the non-listed-algorithm rows, the embedded-<c>jwk</c> row, the unknown-<c>kid</c>
/// row, and the <c>typ</c> rows) — shared by <see cref="TokenValidationTests"/> and
/// <see cref="ChallengeResponseTests"/> so both prove the same catalogue. Since #164 the tokens
/// come from <c>Ostium.Authentication.Jwt.Testing</c>; every row stays unchanged in expectation.
/// </summary>
public static class InvalidTokenCaseCatalog
{
    internal static IEnumerable<(string Label, Func<TestTokenIssuer, string> BuildToken)> Cases()
    {
        yield return ("alg=none, empty signature", issuer => TestTokenIssuer.BuildAlgNoneToken(Genuine(issuer)));
        yield return ("alg=None, empty signature", issuer => TestTokenIssuer.BuildAlgNoneToken(Genuine(issuer), "None"));
        yield return ("alg=NONE, empty signature", issuer => TestTokenIssuer.BuildAlgNoneToken(Genuine(issuer), "NONE"));
        yield return (
            "alg=none, non-empty signature",
            issuer => TestTokenIssuer.BuildAlgNoneToken(Genuine(issuer), includeGarbageSignature: true));

        yield return ("HS256, random secret", issuer => TestTokenIssuer.BuildHs256Token(RandomSecret(), Genuine(issuer)));
        yield return (
            "HS256, realm RSA public key as PEM text",
            issuer => TestTokenIssuer.BuildHs256Token(
                Encoding.ASCII.GetBytes(issuer.RsaPublicKeyPem()), Genuine(issuer), issuer.RsaSigningKey.KeyId));
        yield return (
            "HS256, realm RSA public key as DER bytes",
            issuer => TestTokenIssuer.BuildHs256Token(issuer.RsaPublicKeyDer(), Genuine(issuer), issuer.RsaSigningKey.KeyId));

        yield return (
            "PS256, genuine key, not on the allow-list",
            issuer => TestTokenIssuer.IssueToken(Genuine(issuer), issuer.RsaSigningKey, SecurityAlgorithms.RsaSsaPssSha256));
        yield return (
            "RS512, genuine key, not on the allow-list",
            issuer => TestTokenIssuer.IssueToken(Genuine(issuer), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha512));

        yield return ("embedded attacker jwk header", issuer => issuer.IssueTokenWithEmbeddedAttackerJwk(Genuine(issuer)));
        yield return (
            "unknown kid / signed by a second key",
            issuer => TestTokenIssuer.IssueToken(Genuine(issuer), issuer.OtherRsaSigningKey, SecurityAlgorithms.RsaSha256));

        yield return (
            "wrong audience",
            issuer =>
            {
                var claims = Genuine(issuer);
                claims["aud"] = "some-other-api";
                return TestTokenIssuer.IssueToken(claims, issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
            });
        yield return (
            "wrong issuer",
            issuer =>
            {
                var claims = Genuine(issuer);
                claims["iss"] = "https://wrong-issuer.test/realms/decisya";
                return TestTokenIssuer.IssueToken(claims, issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
            });
        yield return (
            "expired more than 5 minutes ago",
            issuer => TestTokenIssuer.IssueToken(
                Genuine(issuer, expires: issuer.Now().AddMinutes(-6)), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "no exp claim",
            issuer => TestTokenIssuer.IssueToken(
                Genuine(issuer, includeExpiry: false), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "nbf far in the future",
            issuer => TestTokenIssuer.IssueToken(
                Genuine(issuer, notBefore: issuer.Now().AddSeconds(120)), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));

        yield return (
            "typ is ID, not Bearer",
            issuer => TestTokenIssuer.IssueToken(
                Genuine(issuer, tokenType: "ID"), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "no typ claim",
            issuer => TestTokenIssuer.IssueToken(
                Genuine(issuer, tokenType: null), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
    }

    public static IEnumerable<object[]> AsTheoryData() => Cases().Select(c => new object[] { c.Label, c.BuildToken });

    internal static byte[] RandomSecret() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// The package's default claim set plus Decisya's <c>tenant_id</c>, so a row differs from a
    /// genuine token only in what the row varies.
    /// </summary>
    private static Dictionary<string, object> Genuine(
        TestTokenIssuer issuer,
        string? tokenType = "Bearer",
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null,
        bool includeExpiry = true)
    {
        var claims = issuer.DefaultClaims(
            subject: "dev-alice", tokenType: tokenType, expires: expires, notBefore: notBefore, includeExpiry: includeExpiry);
        claims["tenant_id"] = DecisyaTestTokens.DevAliceTenantId;
        return claims;
    }
}
