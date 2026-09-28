using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Story 2's scenario outline rows, plus G3's additions (the algorithm-confusion PEM/DER
/// rows, the non-listed-algorithm rows, the embedded-<c>jwk</c> row, the unknown-<c>kid</c>
/// row, and the <c>typ</c> rows) — shared by <see cref="TokenValidationTests"/> and
/// <see cref="ChallengeResponseTests"/> so both prove the same catalogue.
/// </summary>
public static class InvalidTokenCaseCatalog
{
    internal static IEnumerable<(string Label, Func<TestTokenIssuer, string> BuildToken)> Cases()
    {
        yield return ("alg=none, empty signature", _ => TestTokenIssuer.BuildAlgNoneToken(TestTokenIssuer.DefaultClaims()));
        yield return ("alg=None, empty signature", _ => TestTokenIssuer.BuildAlgNoneToken(TestTokenIssuer.DefaultClaims(), "None"));
        yield return ("alg=NONE, empty signature", _ => TestTokenIssuer.BuildAlgNoneToken(TestTokenIssuer.DefaultClaims(), "NONE"));
        yield return (
            "alg=none, non-empty signature",
            _ => TestTokenIssuer.BuildAlgNoneToken(TestTokenIssuer.DefaultClaims(), includeGarbageSignature: true));

        yield return ("HS256, random secret", _ => TestTokenIssuer.BuildHs256Token(RandomSecret(), TestTokenIssuer.DefaultClaims()));
        yield return (
            "HS256, realm RSA public key as PEM text",
            issuer => TestTokenIssuer.BuildHs256Token(
                Encoding.ASCII.GetBytes(issuer.RsaPublicKeyPem()), TestTokenIssuer.DefaultClaims(), TestTokenIssuer.SigningKeyId));
        yield return (
            "HS256, realm RSA public key as DER bytes",
            issuer => TestTokenIssuer.BuildHs256Token(issuer.RsaPublicKeyDer(), TestTokenIssuer.DefaultClaims(), TestTokenIssuer.SigningKeyId));

        yield return (
            "PS256, genuine key, not on the allow-list",
            issuer => TestTokenIssuer.IssueToken(TestTokenIssuer.DefaultClaims(), issuer.RsaSigningKey, SecurityAlgorithms.RsaSsaPssSha256));
        yield return (
            "RS512, genuine key, not on the allow-list",
            issuer => TestTokenIssuer.IssueToken(TestTokenIssuer.DefaultClaims(), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha512));

        yield return ("embedded attacker jwk header", issuer => issuer.IssueTokenWithEmbeddedAttackerJwk(TestTokenIssuer.DefaultClaims()));
        yield return (
            "unknown kid / signed by a second key",
            issuer => TestTokenIssuer.IssueToken(TestTokenIssuer.DefaultClaims(), issuer.OtherRsaSigningKey, SecurityAlgorithms.RsaSha256));

        yield return (
            "wrong audience",
            issuer => TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(audience: "some-other-api"), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "wrong issuer",
            issuer => TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(issuer: "https://wrong-issuer.test/realms/decisya"), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "expired more than 5 minutes ago",
            issuer => TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(expires: TestTokenIssuer.Now().AddMinutes(-6)), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "no exp claim",
            issuer => TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(includeExpiry: false), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "nbf far in the future",
            issuer => TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(notBefore: TestTokenIssuer.Now().AddSeconds(120)), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));

        yield return (
            "typ is ID, not Bearer",
            issuer => TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(tokenType: "ID"), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
        yield return (
            "no typ claim",
            issuer => TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(tokenType: null), issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
    }

    public static IEnumerable<object[]> AsTheoryData() => Cases().Select(c => new object[] { c.Label, c.BuildToken });

    internal static byte[] RandomSecret() => RandomNumberGenerator.GetBytes(32);
}
