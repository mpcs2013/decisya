using System.Security.Cryptography;

namespace Decisya.Identity.Tests;

/// <summary>Canary values assembled at run time so no realistic secret literal lands in
/// the diff (G4-17-03; mirrors Decisya.ServiceDefaults.Tests/Canaries.cs).</summary>
internal static class Canaries
{
    internal static string Unique(string label) =>
        string.Join('-', "canary", label, Guid.NewGuid().ToString("N"));

    /// <summary>A value that satisfies <c>RealmSecretRules</c>'s charset and length rule,
    /// so it is usable as a canary client-secret or dev-password value in a test that must
    /// prove the value never reaches a message or output stream.</summary>
    internal static string SecretShaped(int length) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(length))[..length];
}
