using System.Security.Cryptography;

namespace Decisya.Bff.Tests;

/// <summary>
/// Resolves the two realm-placeholder secrets the same way
/// <c>Decisya.Identity.Tests/KeycloakRealmFixture.ResolveSecret</c> does (testcontainers
/// skill: "generated per run... a fixture-generated value on the host... In CI a missing
/// value fails the test"). Not linked from <c>RealmSecretRules</c> (G2's csproj table links
/// only <c>ContainerImages.cs</c> into this project), so this is a small, self-contained
/// copy of just the resolution policy, not the shape validation.
/// </summary>
internal static class TestSecrets
{
    internal static string ResolveOrFail(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (IsRunningInCi())
        {
            throw new InvalidOperationException($"{variableName} must be set in CI.");
        }

        return GenerateHex(32);
    }

    private static bool IsRunningInCi() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    private static string GenerateHex(int length) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(length))[..length];
}
