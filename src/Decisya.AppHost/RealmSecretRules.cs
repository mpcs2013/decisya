using System.Text.RegularExpressions;

namespace Decisya.AppHost;

/// <summary>
/// The charset and length rule for the two Keycloak realm-import placeholders
/// (<c>${DECISYA_BFF_CLIENT_SECRET}</c> and <c>${DECISYA_DEV_USER_PASSWORD}</c>,
/// issue #17), shared by the AppHost (which enforces it on the dev-user password before any
/// resource starts) and by <c>Decisya.Identity.Tests</c> (which enforces it on every value
/// its Testcontainers fixture supplies). This file is linked as source into that project.
/// </summary>
/// <remarks>
/// <para>
/// G4 correction (confirmed live against Keycloak 26.7.4, both through the real AppHost and
/// through Testcontainers): the realm file's own <c>${env.VAR}</c> form, which G2 specified
/// from the Keycloak server guide, is <b>not</b> substituted by this version's partial-realm
/// import. It is left in place as literal text, so every seeded user's password and the
/// client secret became the corresponding literal placeholder string — G3 T-01's fail-open,
/// actually observed. The unprefixed <c>${VAR}</c> form (also Keycloak-documented) is
/// substituted correctly and is now what <c>the committed realm export</c> uses.
/// <c>PlaceholderSubstitutionRegressionTests</c> in Decisya.Identity.Tests guards against a
/// regression to either form ever authenticating a user.
/// </para>
/// <para>
/// Values match <c>^[A-Za-z0-9_-]+$</c> only, so a value can never contain <c>"</c>,
/// <c>\</c>, <c>$</c>, <c>{</c> or <c>}</c> and cannot break or inject into the realm JSON,
/// whether Keycloak substitutes before or after JSON tokenisation (G2; G3 T-04). Hex from a
/// CSPRNG satisfies this rule and the realm's password policy.
/// </para>
/// </remarks>
internal static class RealmSecretRules
{
    public const int ClientSecretMinLength = 32;
    public const int ClientSecretMaxLength = 128;
    public const int DevPasswordMinLength = 16;
    public const int DevPasswordMaxLength = 128;

    private static readonly Regex Charset =
        new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The literal placeholder text left in place when the variable is unresolved (G3 T-01).
    /// This is the form Keycloak 26.7.4 actually substitutes (confirmed live, G4); it is what
    /// <c>the committed realm export</c> uses.
    /// </summary>
    public const string BffClientSecretPlaceholderLiteral = "${DECISYA_BFF_CLIENT_SECRET}";

    /// <summary>
    /// The literal placeholder text left in place when the variable is unresolved (G3 T-01).
    /// This is the form Keycloak 26.7.4 actually substitutes (confirmed live, G4); it is what
    /// <c>the committed realm export</c> uses.
    /// </summary>
    public const string DevUserPasswordPlaceholderLiteral = "${DECISYA_DEV_USER_PASSWORD}";

    /// <summary>
    /// G2's originally specified <c>${env.VAR}</c> form. Keycloak 26.7.4 does <b>not</b>
    /// substitute this form in a partial realm import (confirmed live, G4): it is stored
    /// verbatim as the literal credential value, exactly like an unresolved variable would
    /// be. Kept only so the regression guard can prove this legacy form never authenticates
    /// a user either, never used to build the realm file itself.
    /// </summary>
    public const string LegacyEnvPrefixedBffClientSecretPlaceholderLiteral = "${env.DECISYA_BFF_CLIENT_SECRET}";

    /// <summary>See <see cref="LegacyEnvPrefixedBffClientSecretPlaceholderLiteral"/>.</summary>
    public const string LegacyEnvPrefixedDevUserPasswordPlaceholderLiteral = "${env.DECISYA_DEV_USER_PASSWORD}";

    public static bool IsValidClientSecret(string? value) =>
        IsValid(value, ClientSecretMinLength, ClientSecretMaxLength);

    public static bool IsValidDevPassword(string? value) =>
        IsValid(value, DevPasswordMinLength, DevPasswordMaxLength);

    private static bool IsValid(string? value, int minLength, int maxLength) =>
        !string.IsNullOrEmpty(value)
        && value.Length >= minLength
        && value.Length <= maxLength
        && Charset.IsMatch(value);

    /// <summary>
    /// Fails fast, before any AppHost resource starts, when the dev-user password parameter
    /// is missing, blank, or violates <see cref="IsValidDevPassword"/>. The exception message
    /// names only the configuration key and the rule, never the candidate value (CLAUDE.md:
    /// no secret in logs or output; G4-17-02): a canary value passed in never appears in the
    /// message or in the exception's <c>ToString()</c>.
    /// </summary>
    public static void EnsureDevUserPassword(string? value)
    {
        if (!IsValidDevPassword(value))
        {
            throw new InvalidOperationException(
                "Parameters:dev-user-password is missing or does not satisfy RealmSecretRules " +
                $"(charset [A-Za-z0-9_-], length {DevPasswordMinLength}-{DevPasswordMaxLength}). " +
                "Set it once in the AppHost's user-secrets (GETTING-STARTED, \"3. Keycloak\").");
        }
    }
}
