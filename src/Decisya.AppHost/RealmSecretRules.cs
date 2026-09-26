using System.Text.RegularExpressions;

namespace Decisya.AppHost;

/// <summary>
/// The charset and length rule for the two Keycloak realm-import placeholders
/// (<c>${env.DECISYA_BFF_CLIENT_SECRET}</c> and <c>${env.DECISYA_DEV_USER_PASSWORD}</c>,
/// issue #17), shared by the AppHost (which enforces it on the dev-user password before any
/// resource starts) and by <c>Decisya.Identity.Tests</c> (which enforces it on every value
/// its Testcontainers fixture supplies). This file is linked as source into that project.
/// </summary>
/// <remarks>
/// Values match <c>^[A-Za-z0-9_-]+$</c> only, so a value can never contain <c>"</c>,
/// <c>\</c>, <c>$</c>, <c>{</c> or <c>}</c> and cannot break or inject into the realm JSON,
/// whether Keycloak substitutes before or after JSON tokenisation (G2; G3 T-04). Hex from a
/// CSPRNG satisfies this rule and the realm's password policy.
/// </remarks>
internal static class RealmSecretRules
{
    public const int ClientSecretMinLength = 32;
    public const int ClientSecretMaxLength = 128;
    public const int DevPasswordMinLength = 16;
    public const int DevPasswordMaxLength = 128;

    private static readonly Regex Charset =
        new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The literal placeholder text left in place when a variable is unresolved (G3 T-01).</summary>
    public const string BffClientSecretPlaceholderLiteral = "${env.DECISYA_BFF_CLIENT_SECRET}";

    /// <summary>The literal placeholder text left in place when a variable is unresolved (G3 T-01).</summary>
    public const string DevUserPasswordPlaceholderLiteral = "${env.DECISYA_DEV_USER_PASSWORD}";

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
