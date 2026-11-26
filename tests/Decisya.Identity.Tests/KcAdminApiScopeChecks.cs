using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// #349 M-3: the shared assertions for Keycloak 26.8.2's built-in <c>kc-admin-api</c> client scope
/// (mapper <c>oidc-admin-api-audience-mapper</c>), which adds the realm's Admin API address as an
/// extra <c>aud</c> value to any client that has it. Decisya's clients and the realm defaults must
/// never carry it. Both realms' tests read the Admin API data through their fixture's existing
/// master <c>admin-cli</c> access and pass the JSON here.
/// </summary>
internal static class KcAdminApiScopeChecks
{
    public const string ScopeName = "kc-admin-api";

    /// <summary>Keycloak's own admin clients, the only ones allowed to carry the scope.</summary>
    private static readonly string[] BuiltInAdminClients = ["security-admin-console", "admin-cli"];

    /// <summary>Asserts a realm's default scope list (the <c>default-default-client-scopes</c> or <c>default-optional-client-scopes</c> response) has no <c>kc-admin-api</c>.</summary>
    public static void AssertRealmDefaultsExclude(JsonElement scopes, string realm, string listName)
    {
        var names = scopes.EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();

        names.Should().NotContain(ScopeName, $"realm '{realm}' {listName} must not hand the Admin API audience to every client (found: [{string.Join(", ", names)}])");
    }

    /// <summary>
    /// Asserts that no client of the realm carries <c>kc-admin-api</c> among its default or optional
    /// scopes, except the built-in admin clients. Returns which built-ins carry it, for the record.
    /// </summary>
    public static string AssertOnlyBuiltInAdminClientsCarryIt(JsonElement clients, string realm)
    {
        var carriers = new List<string>();
        var unexpected = new List<string>();

        foreach (var client in clients.EnumerateArray())
        {
            var clientId = client.GetProperty("clientId").GetString()!;
            var scopes = ReadNames(client, "defaultClientScopes").Concat(ReadNames(client, "optionalClientScopes"));
            if (!scopes.Contains(ScopeName, StringComparer.Ordinal))
            {
                continue;
            }

            carriers.Add(clientId);
            if (!BuiltInAdminClients.Contains(clientId, StringComparer.Ordinal))
            {
                unexpected.Add(clientId);
            }
        }

        var summary = carriers.Count == 0
            ? "no client carries kc-admin-api"
            : "clients carrying kc-admin-api: " + string.Join(", ", carriers.Order(StringComparer.Ordinal));

        unexpected.Should().BeEmpty($"only Keycloak's built-in {string.Join(" and ", BuiltInAdminClients)} may carry {ScopeName} in realm '{realm}'; {summary}");

        return $"realm '{realm}': {summary}";
    }

    private static IEnumerable<string> ReadNames(JsonElement client, string property) =>
        client.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString()!)
            : [];
}
