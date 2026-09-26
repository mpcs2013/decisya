namespace Decisya.SharedKernel.Tests;

/// <summary>
/// Canary values the Tenancy and Results tests look for. Every one is assembled at run
/// time so no file in the diff contains a realistic secret-shaped or hostile literal
/// (mirrors <c>Decisya.ServiceDefaults.Tests.Canaries</c>).
/// </summary>
internal static class Canaries
{
    /// <summary>A value that must never reach an exception message or a serialized payload.</summary>
    internal static string Unique(string label) =>
        string.Join('-', "canary", label, Guid.NewGuid().ToString("N"));

    /// <summary>A JWT-shaped string, assembled so it is not a literal token in source.</summary>
    internal static string JwtShaped() => string.Join(
        '.',
        string.Concat("ey", "JhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"),
        string.Concat("eyJzdWIiOiIxMjM0NTY3", "ODkwIiwibmFtZSI6IkNhbmFyeSJ9"),
        string.Concat("Q2FuYXJ5U2lnbmF0", "dXJlVmFsdWU"));
}
