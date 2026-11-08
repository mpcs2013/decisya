using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #268, S-268-01: Keycloak 26.8 enables the <c>scim-api</c> feature by default. The production
/// stack sets <c>KC_FEATURES_DISABLED=scim-api</c>; this proves the running server reports it as off.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ProductionKeycloakDefinition.Name)]
public sealed class ProductionFeatureTests
{
    private readonly ProductionKeycloak _stack;

    public ProductionFeatureTests(ProductionKeycloak stack)
    {
        _stack = stack;
    }

    [Fact]
    public async Task The_scim_api_feature_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);

        using var admin = await _stack.AdminClientAsync(ct);
        var serverInfo = await ProductionKeycloak.GetJsonAsync(admin, "/admin/serverinfo", ct);

        // The server-info shape differs between releases (profileInfo lists, or objects with name/enabled),
        // so the feature is found by name anywhere in the document, case-insensitive, scim-api or SCIM_API.
        var findings = new List<(string Where, bool Enabled)>();
        Walk(serverInfo, "$", null, findings);

        findings.Should().NotBeEmpty("server info must list the scim-api feature so its state can be checked");
        findings.Should().OnlyContain(f => !f.Enabled, "scim-api must be disabled; found: " + string.Join(", ", findings.Select(f => $"{f.Where}={(f.Enabled ? "enabled" : "disabled")}")));
    }

    private static bool IsScim(string? value) =>
        value is not null && value.Replace('-', '_').Equals("SCIM_API", StringComparison.OrdinalIgnoreCase);

    private static void Walk(JsonElement element, string path, string? arrayName, List<(string, bool)> findings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && IsScim(name.GetString()))
                {
                    var enabled = element.TryGetProperty("enabled", out var flag)
                        ? flag.ValueKind == JsonValueKind.True
                        : !IsDisabledList(arrayName);
                    findings.Add((path, enabled));
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (IsScim(property.Name) && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        findings.Add((path + "." + property.Name, property.Value.ValueKind == JsonValueKind.True));
                        continue;
                    }

                    Walk(property.Value, path + "." + property.Name, property.Name, findings);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, path + "[]", arrayName, findings);
                }

                break;
            case JsonValueKind.String when IsScim(element.GetString()):
                // A bare name in a list: only the list called disabledFeatures means "off".
                findings.Add((path, !IsDisabledList(arrayName)));
                break;
        }
    }

    private static bool IsDisabledList(string? arrayName) =>
        arrayName is not null && arrayName.Contains("disabled", StringComparison.OrdinalIgnoreCase);
}
