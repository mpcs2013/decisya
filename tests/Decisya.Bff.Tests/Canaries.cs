namespace Decisya.Bff.Tests;

/// <summary>Canary values assembled at run time so no realistic secret literal lands in the
/// diff (mirrors <c>Decisya.Identity.Tests/Canaries.cs</c>).</summary>
internal static class Canaries
{
    internal static string Unique(string label) =>
        string.Join('-', "canary", label, Guid.NewGuid().ToString("N"));
}
