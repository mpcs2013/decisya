using System.Text.Json;
using System.Text.Json.Nodes;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, G2 D1: the production realm file is a copy of the dev realm with exactly the
/// differences P1 to P8 and no others. A deep comparison by JSON path; the allow-list is pinned by
/// a meta-test (like <see cref="RealmGuardTests"/>'s exemption list), so widening it is a reviewed
/// diff of this file. Reports paths, never values. No Docker.
/// </summary>
public class ProductionRealmParityTests
{
    private const string UserProfileConfigPath =
        "components/org.keycloak.userprofile.UserProfileProvider/declarative-user-profile/config/kc.user.profile.config";

    /// <summary>
    /// One named, allowed difference: its id, its reason and the path prefixes it covers.
    /// <paramref name="ReservedPrefixes"/> are allowed but no real difference uses them today (so the
    /// "every allowed path is used" check skips them): a reserved slot needs a reason, like P8.
    /// </summary>
    internal sealed record AllowedDifference(string Id, string Reason, string[] PathPrefixes, string[]? ReservedPrefixes = null)
    {
        internal IEnumerable<string> AllPrefixes => PathPrefixes.Concat(ReservedPrefixes ?? []);
    }

    /// <summary>The allow-list (G2 D1, P1 to P8). Paths use '/' between segments; arrays of objects are keyed by clientId, alias, name, client, username or providerId.</summary>
    internal static readonly AllowedDifference[] ParityAllowList =
    [
        new("P1", "users", ["users"]),
        new("P2", "TLS", ["sslRequired"]),
        new(
            "P3",
            "hostname-derived URIs",
            [
                "clients/decisya-bff/redirectUris",
                "clients/decisya-bff/attributes/post.logout.redirect.uris",
                "clients/decisya-bff/attributes/backchannel.logout.url",
            ]),
        new(
            "P4",
            "MFA",
            ["browserFlow", "authenticationFlows", "authenticatorConfig", "attributes/acr.loa.map"],
            // The production file sets no requiredActions: when a realm file lists any, Keycloak 26.7.5
            // does not add its defaults (observed: UPDATE_PASSWORD was missing), and CONFIGURE_TOTP is
            // enabled by default anyway (pinned by ProductionRealmImportTests). Reserved, not used.
            ReservedPrefixes: ["requiredActions"]),
        new("P5", "password policy", ["passwordPolicy"]),
        new(
            "P6",
            "events",
            [
                "eventsEnabled", "eventsExpiration", "eventsListeners", "enabledEventTypes",
                "adminEventsEnabled", "adminEventsDetailsEnabled", "attributes/adminEventsExpiration",
            ]),
        new("P7", "user profile", [UserProfileConfigPath + "/attributes/synthetic"]),
        new("P8", "secret placeholders: none today, both files use ${DECISYA_BFF_CLIENT_SECRET}", []),
    ];

    private static readonly string[] ArrayKeys = ["clientId", "alias", "name", "client", "username", "providerId"];

    [Fact]
    public void The_allow_list_is_exactly_P1_to_P8()
    {
        ParityAllowList.Select(entry => entry.Id).Should().Equal("P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8");

        ParityAllowList.SelectMany(entry => entry.AllPrefixes).Should().Equal(
            "users",
            "sslRequired",
            "clients/decisya-bff/redirectUris",
            "clients/decisya-bff/attributes/post.logout.redirect.uris",
            "clients/decisya-bff/attributes/backchannel.logout.url",
            "browserFlow", "authenticationFlows", "authenticatorConfig", "attributes/acr.loa.map", "requiredActions",
            "passwordPolicy",
            "eventsEnabled", "eventsExpiration", "eventsListeners", "enabledEventTypes",
            "adminEventsEnabled", "adminEventsDetailsEnabled", "attributes/adminEventsExpiration",
            UserProfileConfigPath + "/attributes/synthetic");

        ParityAllowList.Single(entry => entry.Id == "P8").AllPrefixes.Should().BeEmpty(
            "a future placeholder difference needs a reviewed list change");
        ParityAllowList.Where(entry => entry.ReservedPrefixes is { Length: > 0 }).Select(entry => entry.Id).Should().Equal("P4");
    }

    [Fact]
    public void Production_differs_from_dev_only_on_the_allowed_paths()
    {
        var differences = Compare(LoadDev(), LoadProduction());

        var unexpected = differences.Where(path => !IsAllowed(path)).ToList();

        unexpected.Should().BeEmpty("only P1 to P8 may differ between the dev and the production realm; offending paths: " + string.Join(", ", unexpected));
    }

    /// <summary>Every allowed entry is actually used, so the list can not silently drift wider than the files need.</summary>
    [Fact]
    public void Every_allowed_difference_except_P8_matches_at_least_one_real_difference()
    {
        var differences = Compare(LoadDev(), LoadProduction());

        foreach (var entry in ParityAllowList.Where(entry => entry.PathPrefixes.Length > 0))
        {
            foreach (var prefix in entry.PathPrefixes)
            {
                differences.Should().Contain(path => Covers(prefix, path), $"{entry.Id} ({entry.Reason}): '{prefix}' matches no real difference");
            }
        }
    }

    [Fact]
    public void Production_keeps_the_dev_values_that_the_security_review_relies_on()
    {
        var production = LoadProduction();

        production["sslRequired"]!.GetValue<string>().Should().Be("all");
        production["accessTokenLifespan"]!.GetValue<int>().Should().Be(300);
        production["ssoSessionIdleTimeout"]!.GetValue<int>().Should().Be(1800);
        production["ssoSessionMaxLifespan"]!.GetValue<int>().Should().Be(36000);
        production["registrationAllowed"]!.GetValue<bool>().Should().BeFalse();
        production["resetPasswordAllowed"]!.GetValue<bool>().Should().BeFalse();
        production["rememberMe"]!.GetValue<bool>().Should().BeFalse();
        production["bruteForceProtected"]!.GetValue<bool>().Should().BeTrue();
        production["otpPolicyType"]!.GetValue<string>().Should().Be("totp");

        var bff = production["clients"]!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == "decisya-bff")!.AsObject();
        bff["implicitFlowEnabled"]!.GetValue<bool>().Should().BeFalse();
        bff["directAccessGrantsEnabled"]!.GetValue<bool>().Should().BeFalse();
        bff["serviceAccountsEnabled"]!.GetValue<bool>().Should().BeFalse();
        bff["webOrigins"]!.AsArray().Should().BeEmpty();
        bff["secret"]!.GetValue<string>().Should().Be("${DECISYA_BFF_CLIENT_SECRET}");
    }

    [Fact]
    public void The_comparer_reports_a_difference_the_list_does_not_allow()
    {
        var production = LoadProduction();
        production["loginWithEmailAllowed"] = false;
        production["clients"]!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == "decisya-bff")!["fullScopeAllowed"] = true;
        production["roles"]!["realm"]!.AsArray().Add(new JsonObject { ["name"] = "extra-role" });

        var unexpected = Compare(LoadDev(), production).Where(path => !IsAllowed(path)).ToList();

        unexpected.Should().Contain("loginWithEmailAllowed");
        unexpected.Should().Contain("clients/decisya-bff/fullScopeAllowed");
        unexpected.Should().Contain(path => path.StartsWith("roles/realm/extra-role", StringComparison.Ordinal));
    }

    [Fact]
    public void The_comparer_matches_clients_by_clientId_not_by_position()
    {
        var dev = LoadDev();
        var production = LoadProduction();
        var clients = production["clients"]!.AsArray();
        var first = clients[0]!;
        var second = clients[1]!;
        clients.Clear();
        clients.Add(second.DeepClone());
        clients.Add(first.DeepClone());

        Compare(dev, production).Where(path => !IsAllowed(path)).Should().BeEmpty();
    }

    [Fact]
    public void A_profile_attribute_other_than_synthetic_is_not_an_allowed_difference()
    {
        var production = LoadProduction();
        var profile = ProfileOf(production);
        profile["attributes"]!.AsArray().Add(new JsonObject { ["name"] = "extra" });
        SetProfile(production, profile);

        var unexpected = Compare(LoadDev(), production).Where(path => !IsAllowed(path)).ToList();

        unexpected.Should().ContainSingle().Which.Should().EndWith("/attributes/extra/name");
    }

    // ------------------------------------------------------------------ comparer

    private static bool IsAllowed(string path) =>
        ParityAllowList.SelectMany(entry => entry.AllPrefixes).Any(prefix => Covers(prefix, path));

    private static bool Covers(string prefix, string path) =>
        string.Equals(path, prefix, StringComparison.Ordinal)
        || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static List<string> Compare(JsonObject dev, JsonObject production)
    {
        var left = Flatten(dev);
        var right = Flatten(production);

        var differing = new List<string>();
        foreach (var path in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var inLeft = left.TryGetValue(path, out var leftValue);
            var inRight = right.TryGetValue(path, out var rightValue);
            if (inLeft != inRight || !string.Equals(leftValue, rightValue, StringComparison.Ordinal))
            {
                differing.Add(path);
            }
        }

        return differing;
    }

    private static Dictionary<string, string> Flatten(JsonNode root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, string.Empty, result);
        return result;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> result)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    result[path] = "{}";
                }

                foreach (var property in obj)
                {
                    Walk(property.Value, Join(path, property.Key), result);
                }

                break;

            case JsonArray array:
                if (path.EndsWith("/kc.user.profile.config", StringComparison.Ordinal) && array.All(item => item is JsonValue))
                {
                    // The embedded user-profile document: compared after parsing, attribute by attribute (P7).
                    foreach (var item in array)
                    {
                        Walk(JsonNode.Parse(item!.GetValue<string>()), path, result);
                    }

                    break;
                }

                var keys = array.Select(item => KeyOf(item)).ToList();
                if (array.Count > 0 && keys.All(key => key is not null))
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        Walk(array[i], Join(path, keys[i]!), result);
                    }
                }
                else
                {
                    result[path] = array.ToJsonString();
                }

                break;

            default:
                result[path] = node is null ? "null" : node.ToJsonString();
                break;
        }
    }

    private static string? KeyOf(JsonNode? item)
    {
        if (item is not JsonObject obj)
        {
            return null;
        }

        foreach (var key in ArrayKeys)
        {
            if (obj.TryGetPropertyValue(key, out var value) && value is JsonValue text && text.TryGetValue<string>(out var s))
            {
                return s;
            }
        }

        return null;
    }

    private static string Join(string path, string segment) => path.Length == 0 ? segment : path + "/" + segment;

    // ------------------------------------------------------------------ files

    private static JsonObject LoadDev() => Load(Path.Combine("deploy", "keycloak", "decisya-realm.json"));

    private static JsonObject LoadProduction() => Load(Path.Combine("deploy", "keycloak", "production", "realm-decisya.json"));

    private static JsonObject Load(string relative) =>
        JsonNode.Parse(File.ReadAllText(RepoPaths.Find(relative)))!.AsObject();

    private static JsonObject ProfileOf(JsonObject realm) =>
        JsonNode.Parse(ProfileArray(realm)[0]!.GetValue<string>())!.AsObject();

    private static void SetProfile(JsonObject realm, JsonObject profile) =>
        ProfileArray(realm)[0] = profile.ToJsonString(new JsonSerializerOptions());

    private static JsonArray ProfileArray(JsonObject realm) =>
        realm["components"]!["org.keycloak.userprofile.UserProfileProvider"]!.AsArray()[0]!["config"]!["kc.user.profile.config"]!.AsArray();
}
