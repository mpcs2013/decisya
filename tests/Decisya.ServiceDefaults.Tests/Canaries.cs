using System.Security.Cryptography;

namespace Decisya.ServiceDefaults.Tests;

/// <summary>
/// Canary values the masking tests look for. Every one is assembled at run time from
/// fragments or generated randomly, so no file in the diff contains a realistic secret
/// literal and gitleaks stays green (G4-15-27, and the "test canaries" rule in the G3
/// threat model).
/// </summary>
internal static class Canaries
{
    /// <summary>A value that must never reach a sink unmasked.</summary>
    internal static string Unique(string label) =>
        string.Join('-', "canary", label, Guid.NewGuid().ToString("N"));

    /// <summary>A JWT-shaped string, assembled so it is not a literal token in source.</summary>
    internal static string JwtShaped() => string.Join(
        '.',
        string.Concat("ey", "JhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"),
        string.Concat("eyJzdWIiOiIxMjM0NTY3", "ODkwIiwibmFtZSI6IkNhbmFyeSJ9"),
        string.Concat("Q2FuYXJ5U2lnbmF0", "dXJlVmFsdWU"));

    /// <summary>A <c>Bearer &lt;token&gt;</c>-shaped string.</summary>
    internal static string BearerShaped() =>
        string.Concat("Bea", "rer ", "cAnAry", Guid.NewGuid().ToString("N"));

    /// <summary>A valid base64 HMAC key, generated fresh for each call.</summary>
    internal static string HashKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>A base64 value that is well-formed but too short to be a key.</summary>
    internal static string ShortHashKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
}
