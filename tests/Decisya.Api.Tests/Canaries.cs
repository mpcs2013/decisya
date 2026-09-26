using System.Security.Cryptography;

namespace Decisya.Api.Tests;

/// <summary>Canary values assembled at run time so no realistic secret literal lands in
/// the diff (G4-15-27; mirrors Decisya.ServiceDefaults.Tests/Canaries.cs).</summary>
internal static class Canaries
{
    internal static string Unique(string label) =>
        string.Join('-', "canary", label, Guid.NewGuid().ToString("N"));

    internal static string HashKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
