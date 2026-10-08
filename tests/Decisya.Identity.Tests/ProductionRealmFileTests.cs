using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, G2 D1 and G1 Story 1: static checks over the production realm file and the wrapper
/// that feeds it. No Docker. Findings name the JSON path, never the value.
/// </summary>
public partial class ProductionRealmFileTests
{
    private static readonly string[] AllowedPlaceholders = ["DECISYA_REALM_APP_ORIGIN", "DECISYA_BFF_CLIENT_SECRET"];

    [GeneratedRegex(@"\$\{([^}]*)\}")]
    private static partial Regex PlaceholderPattern();

    [Fact]
    public void The_file_holds_no_user_no_credential_and_no_dev_data()
    {
        var root = LoadRealm();
        var findings = new List<string>();

        root.ContainsKey("users").Should().BeFalse("the production realm seeds no users");

        Walk(root, string.Empty, (path, key, value) =>
        {
            if (key is "credentials" or "secretData" or "hashedSaltedValue" or "credentialData" or "salt")
            {
                findings.Add($"{path}: property '{key}' (credential material)");
            }

            if (key == "type" && value is JsonValue typeValue && typeValue.TryGetValue<string>(out var type) && type is "password" or "otp")
            {
                findings.Add($"{path}: type is a credential type");
            }

            if (key == "username")
            {
                findings.Add($"{path}: a username");
            }

            if (value is JsonValue text && text.TryGetValue<string>(out var s))
            {
                if (s.Contains("dev-", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add($"{path}: contains 'dev-'");
                }

                if (s.Contains(".test", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add($"{path}: contains '.test'");
                }
            }

            if (key.Contains("dev-", StringComparison.OrdinalIgnoreCase) || key.Contains(".test", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add($"{path}: the key itself looks like dev data");
            }
        });

        findings.Should().BeEmpty();
    }

    [Fact]
    public void The_raw_text_has_no_dev_user_no_test_address_and_no_dev_password_placeholder()
    {
        var text = File.ReadAllText(RealmPath());

        text.Should().NotContainEquivalentOf("dev-alice").And.NotContainEquivalentOf("dev-bob").And.NotContainEquivalentOf("dev-admin");
        text.Should().NotContain("DECISYA_DEV_USER_PASSWORD");
        text.Should().NotContain(".test\"");
        text.Should().NotContain("@decisya.test");
    }

    [Fact]
    public void Every_placeholder_is_a_plain_known_name_with_no_default_and_at_most_one_per_string()
    {
        var text = File.ReadAllText(RealmPath());
        var root = LoadRealm();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<string>();

        // Every "${" in the raw text, keys included, must be a well-formed placeholder we know.
        Regex.Count(text, @"\$\{", RegexOptions.None, TimeSpan.FromSeconds(2)).Should().Be(
            PlaceholderPattern().Count(text), "every dollar-brace opening must close");

        Walk(root, string.Empty, (path, key, value) =>
        {
            if (key.Contains("${", StringComparison.Ordinal))
            {
                findings.Add($"{path}: a placeholder in a property name");
            }

            if (value is not JsonValue json || !json.TryGetValue<string>(out var s))
            {
                return;
            }

            var matches = PlaceholderPattern().Matches(s);
            if (matches.Count > 1)
            {
                findings.Add($"{path}: more than one placeholder in one string");
            }

            foreach (Match match in matches)
            {
                var name = match.Groups[1].Value;
                if (name.Contains(':', StringComparison.Ordinal))
                {
                    findings.Add($"{path}: a placeholder with a default or modifier (':' inside)");
                }
                else if (!AllowedPlaceholders.Contains(name))
                {
                    findings.Add($"{path}: an unknown placeholder name");
                }
                else
                {
                    seen.Add(name);
                }
            }
        });

        findings.Should().BeEmpty();
        seen.Should().BeEquivalentTo(AllowedPlaceholders, "both placeholders are used, and nothing else");
    }

    [Fact]
    public void Each_placeholder_stands_alone_in_its_value_or_follows_nothing_but_an_origin()
    {
        var root = LoadRealm();

        var bff = root["clients"]!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == "decisya-bff")!.AsObject();
        bff["secret"]!.GetValue<string>().Should().Be("${DECISYA_BFF_CLIENT_SECRET}");
        bff["redirectUris"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("${DECISYA_REALM_APP_ORIGIN}/signin-oidc");

        var attributes = bff["attributes"]!.AsObject();
        attributes["post.logout.redirect.uris"]!.GetValue<string>().Should().Be("${DECISYA_REALM_APP_ORIGIN}/signout-callback-oidc");
        attributes["backchannel.logout.url"]!.GetValue<string>().Should().Be("${DECISYA_REALM_APP_ORIGIN}/bff/backchannel-logout");
    }

    /// <summary>G4-121-03 (c): the wrapper exports exactly the placeholders the realm file uses.</summary>
    [Fact]
    public void The_wrapper_exports_exactly_the_placeholders_the_realm_uses()
    {
        var wrapper = CodeLines(File.ReadAllText(WrapperPath())).ToList();

        var exportLine = wrapper.Single(line => line.TrimStart().StartsWith("export DECISYA_", StringComparison.Ordinal));
        var exported = exportLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        exported.Should().BeEquivalentTo(AllowedPlaceholders);

        foreach (var name in AllowedPlaceholders)
        {
            wrapper.Should().Contain(line => line.Contains($"${{{name}+set}}", StringComparison.Ordinal),
                $"the wrapper refuses a pre-set {name}");
        }
    }

    [Fact]
    public void The_wrapper_has_no_trace_no_value_echo_and_one_exec_that_adds_import_realm_itself()
    {
        var raw = File.ReadAllText(WrapperPath());
        raw.Should().NotContain("\r", "the wrapper is LF only (.gitattributes)");

        var code = CodeLines(raw).ToList();
        code.Should().NotContain(line => line.Contains("set -x", StringComparison.Ordinal) || line.Contains("xtrace", StringComparison.Ordinal));
        code.Should().NotContain(line => line.Contains("printf", StringComparison.Ordinal));

        // The only echo is die's, with a fixed message: its argument is the function's own $1.
        code.Where(line => line.Contains("echo", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Trim().Should().Be("echo \"entrypoint-stack: $1\" >&2");

        // Every die() message is a fixed string: no expansion of anything but the variable's own name.
        foreach (var line in code.Where(line => line.TrimStart().StartsWith("die ", StringComparison.Ordinal)))
        {
            line.Should().NotMatchRegex(@"\$[A-Za-z_{(]", $"die must not interpolate a value: {line.Trim()}");
        }

        code.Count(line => line.TrimStart().StartsWith("exec ", StringComparison.Ordinal)).Should().Be(1);
        code[^1].Trim().Should().Be("exec /opt/keycloak/bin/kc.sh start --import-realm \"$@\"");
        code.Should().NotContain(line => line.Contains("start-dev", StringComparison.Ordinal) && line.Contains("exec", StringComparison.Ordinal));

        // The caller's --import-realm stays refused: it appears in the refusal list and in the final exec, nowhere else.
        code.Where(line => line.Contains("--import-realm", StringComparison.Ordinal)).Should().HaveCount(2);
    }

    [Fact]
    public void The_password_list_has_no_carriage_return_so_a_windows_checkout_can_not_break_the_policy()
    {
        var bytes = File.ReadAllBytes(RepoPaths.Find(Path.Combine("deploy", "keycloak", "production", "common-passwords.txt")));

        bytes.Should().NotContain((byte)'\r');
        bytes.Length.Should().BeGreaterThan(0);
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'));

    private static string RealmPath() => RepoPaths.Find(Path.Combine("deploy", "keycloak", "production", "realm-decisya.json"));

    private static string WrapperPath() => RepoPaths.Find(Path.Combine("deploy", "keycloak", "entrypoint-stack.sh"));

    private static JsonObject LoadRealm() => JsonNode.Parse(File.ReadAllText(RealmPath()))!.AsObject();

    /// <summary>Visits every property of every object (the key and the value) with its JSON path.</summary>
    private static void Walk(JsonNode? node, string path, Action<string, string, JsonNode?> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    var childPath = path.Length == 0 ? property.Key : path + "." + property.Key;
                    visit(childPath, property.Key, property.Value);
                    Walk(property.Value, childPath, visit);
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var childPath = $"{path}[{i}]";
                    if (array[i] is JsonValue)
                    {
                        visit(childPath, string.Empty, array[i]);
                    }

                    Walk(array[i], childPath, visit);
                }

                break;
        }
    }
}
