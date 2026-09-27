namespace Decisya.Bff.Tests;

/// <summary>A syntactically valid, Docker-free configuration for any test that only needs
/// the host to start (not to actually reach Keycloak or Redis).</summary>
internal static class TestConfiguration
{
    internal static Dictionary<string, string?> GoodOverrides() => new()
    {
        ["Bff:Oidc:Authority"] = "https://example.test/realms/decisya",
        ["Bff:Oidc:ClientId"] = "decisya-bff",
        ["Bff:Oidc:ClientSecret"] = Canaries.Unique("client-secret"),
        ["Bff:Oidc:RequireHttpsMetadata"] = "true",
        ["Bff:DataProtection:KeyRingPath"] = Path.Combine(Path.GetTempPath(), "decisya-bff-tests-keys"),
        ["ConnectionStrings:redis"] = "localhost:6379",
    };
}
