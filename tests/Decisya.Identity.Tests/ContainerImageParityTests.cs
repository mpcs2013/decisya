using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Decisya.AppHost;

namespace Decisya.Identity.Tests;

/// <summary>
/// ADR-0007's image-equality test, widened to Keycloak by issue #17 (G2, G4-17-17): the
/// AppHost, this project's Testcontainers fixture (through the linked
/// <see cref="ContainerImages"/> source) and the agent sandbox's image allow-list
/// (<c>.devcontainer/engine/images.Dockerfile</c>) must all resolve to the exact same image
/// reference.
/// </summary>
[Trait("Category", "Unit")]
public class ContainerImageParityTests
{
    private static readonly Regex FromLine = new(
        @"^FROM\s+(?<reference>\S+)\s+AS\s+(?<alias>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static string ReadImagesDockerfile() =>
        File.ReadAllText(RepoPaths.Find(Path.Combine(".devcontainer", "engine", "images.Dockerfile")));

    private static Dictionary<string, string> ParseAliasToReference()
    {
        var content = ReadImagesDockerfile();
        return FromLine.Matches(content)
            .ToDictionary(m => m.Groups["alias"].Value, m => m.Groups["reference"].Value);
    }

    [Fact]
    public void The_sandbox_image_list_has_a_postgres_alias_matching_ContainerImages()
    {
        var aliases = ParseAliasToReference();

        aliases.Should().ContainKey("postgres");
        aliases["postgres"].Should().Be(ContainerImages.Reference(
            ContainerImages.PostgresRegistry, ContainerImages.PostgresImage,
            ContainerImages.PostgresTag, ContainerImages.PostgresSha256));
    }

    [Fact]
    public void The_sandbox_image_list_has_a_keycloak_alias_matching_ContainerImages()
    {
        var aliases = ParseAliasToReference();

        aliases.Should().ContainKey(
            "keycloak",
            "devops adds this line to .devcontainer/engine/images.Dockerfile in the same PR (#17 G4, devops step)");
        aliases["keycloak"].Should().Be(ContainerImages.Reference(
            ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
            ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256));
    }

    [Fact]
    public void The_sandbox_image_list_has_a_redis_alias_matching_ContainerImages()
    {
        var aliases = ParseAliasToReference();

        aliases.Should().ContainKey(
            "redis",
            "devops adds this line to .devcontainer/engine/images.Dockerfile in the same PR (#18 G4, devops step)");
        aliases["redis"].Should().Be(ContainerImages.Reference(
            ContainerImages.RedisRegistry, ContainerImages.RedisImage,
            ContainerImages.RedisTag, ContainerImages.RedisSha256));
    }

    [Fact]
    public void The_sandbox_image_list_has_a_caddy_alias_matching_ContainerImages()
    {
        var aliases = ParseAliasToReference();

        aliases.Should().ContainKey(
            "caddy",
            "the orchestrator adds this line to .devcontainer/engine/images.Dockerfile in the same PR (#120, D2)");
        aliases["caddy"].Should().Be(ContainerImages.Reference(
            ContainerImages.CaddyRegistry, ContainerImages.CaddyImage,
            ContainerImages.CaddyTag, ContainerImages.CaddySha256));
    }

    [Fact]
    public void The_sandbox_image_list_has_an_otelcollector_alias_matching_ContainerImages()
    {
        var aliases = ParseAliasToReference();

        aliases.Should().ContainKey(
            "otelcollector",
            "the orchestrator adds this line to .devcontainer/engine/images.Dockerfile in the same PR (#120, D2)");
        aliases["otelcollector"].Should().Be(ContainerImages.Reference(
            ContainerImages.OtelCollectorRegistry, ContainerImages.OtelCollectorImage,
            ContainerImages.OtelCollectorTag, ContainerImages.OtelCollectorSha256));
    }

    [Fact]
    public void The_Caddy_and_collector_references_are_exact_patch_tags_on_docker_io()
    {
        ContainerImages.CaddyRegistry.Should().Be("docker.io");
        ContainerImages.CaddyImage.Should().Be("library/caddy");
        ContainerImages.CaddyTag.Should().MatchRegex(@"^2\.\d+\.\d+-alpine$");

        ContainerImages.OtelCollectorRegistry.Should().Be("docker.io");
        ContainerImages.OtelCollectorImage.Should().Be("otel/opentelemetry-collector");
        ContainerImages.OtelCollectorTag.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }

    /// <summary>
    /// #142, G3 M-10: the collector is pinned to the fixed 0.163.1 build (go1.26.9, x/net v0.61.0),
    /// so it needs and carries no Go exception. The committed Compose file carries the same pin.
    /// </summary>
    [Fact]
    public void The_collector_is_pinned_to_0_163_1_with_its_digest_in_ContainerImages_the_sandbox_list_and_Compose()
    {
        const string digest = "f0e23036dce3db3f9cb80d907c75a0d034cd32e93511a1943482d46129b9ae9c";
        const string reference = "docker.io/otel/opentelemetry-collector:0.163.1@sha256:" + digest;

        ContainerImages.OtelCollectorTag.Should().Be("0.163.1");
        ContainerImages.OtelCollectorSha256.Should().Be(digest);
        ParseAliasToReference()["otelcollector"].Should().Be(reference);

        var compose = File.ReadAllText(
            RepoPaths.Find(Path.Combine("deploy", "compose", "docker-compose.yaml")));
        compose.Should().Contain(reference);
    }

    /// <summary>
    /// #142, G1 story 5: ADR-0015 records the accepted item 4 with the 2026-10-31 fallback and the
    /// 2026-11-30 sunset.
    /// </summary>
    [Fact]
    public void ADR_0015_records_item_4_with_the_fallback_and_the_sunset()
    {
        var adr = File.ReadAllText(
            RepoPaths.Find(Path.Combine("docs", "adr", "0015-container-image-cve-scan-grype.md")));

        adr.Should().Contain("item 4");
        adr.Should().Contain("2026-10-31");
        adr.Should().Contain("2026-11-30");
    }

    /// <summary>
    /// #183, G3 M-1: the zlib CVE-2026-85091 exceptions are gone (the bumped digests carry the fix),
    /// and the Caddy curl CVE-2026-92392 exception is the single approved entry with an expiry of
    /// at most 30 days, no later than 2026-11-21.
    /// </summary>
    [Fact]
    public void The_exceptions_file_has_no_zlib_CVE_2026_85091_entry_and_one_approved_30_day_caddy_curl_CVE_2026_92392_entry()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            RepoPaths.Find(Path.Combine(".github", "image-scan", "exceptions.json"))));
        var entries = document.RootElement.GetProperty("exceptions").EnumerateArray().ToList();

        entries.Where(e => e.GetProperty("id").GetString() == "CVE-2026-85091")
            .Should().BeEmpty();

        var curl = entries.Where(e => e.GetProperty("id").GetString() == "CVE-2026-92392"
                                   && e.GetProperty("image").GetString() == "caddy"
                                   && e.GetProperty("package").GetString() == "curl").ToList();
        curl.Should().ContainSingle();
        var entry = curl[0];
        entry.GetProperty("image").GetString().Should().Be("caddy");
        entry.GetProperty("package").GetString().Should().Be("curl");
        entry.GetProperty("issue").GetString().Should().Be("#187");

        var justification = entry.GetProperty("justification").GetString()!;
        Regex.Matches(justification, @"approved by Marco \d{4}-\d{2}-\d{2}").Should().HaveCount(1);
        justification.Should().NotContain("PENDING");

        var added = DateOnly.ParseExact(
            entry.GetProperty("added").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expires = DateOnly.ParseExact(
            entry.GetProperty("expires").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        (expires.DayNumber - added.DayNumber).Should().BeLessThanOrEqualTo(30);
        expires.Should().BeOnOrBefore(DateOnly.ParseExact("2026-11-21", "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// #187, G3 M-3 (replaces the removed .claude test of the #142 entries): the bridge entries are
    /// exactly the 20 approved Caddy entries (17 item 4, 3 item 3), all added 2026-10-29 and ending
    /// by 2026-11-12, each with exactly one Marco approval and no PENDING; no #142 or #183 entry
    /// remains and the collector carries no Go exception. Selects by issue, so #212's Caddy
    /// go-jose v3 entry on the stacked branch does not affect it.
    /// </summary>
    [Fact]
    public void The_exceptions_file_has_exactly_the_20_approved_187_caddy_bridge_entries_and_no_142_183_or_collector_Go_entry()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            RepoPaths.Find(Path.Combine(".github", "image-scan", "exceptions.json"))));
        var entries = document.RootElement.GetProperty("exceptions").EnumerateArray().ToList();

        static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
        static DateOnly Day(string value) =>
            DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var bridge = entries.Where(e => Str(e, "issue") == "#187").ToList();

        bridge.Should().HaveCount(20);
        bridge.Select(e => Str(e, "image")).Should().OnlyContain(image => image == "caddy");

        var itemThreePackages = new[] { "brotli-libs", "curl", "libcurl" };
        var itemThree = bridge.Where(e => itemThreePackages.Contains(Str(e, "package"))).ToList();
        var itemFour = bridge.Except(itemThree).ToList();

        itemThree.Select(e => Str(e, "package")).Should().BeEquivalentTo(itemThreePackages);
        itemThree.Should().HaveCount(3);

        itemFour.Should().HaveCount(17);
        itemFour.Count(e => Str(e, "package") == "stdlib").Should().Be(11);
        itemFour.Count(e => Str(e, "package") == "golang.org/x/net").Should().Be(5);
        var jose = itemFour.Where(e => Str(e, "package") == "github.com/go-jose/go-jose/v4").ToList();
        jose.Should().ContainSingle();
        Str(jose[0], "id").Should().Be("GHSA-mgcc-cww5-fp2q");

        foreach (var entry in bridge)
        {
            var label = Str(entry, "id") + " " + Str(entry, "package");
            Str(entry, "added").Should().Be("2026-10-29", label);
            Day(Str(entry, "expires")).Should().BeOnOrBefore(Day("2026-11-12"), label);
            var justification = Str(entry, "justification");
            Regex.Matches(justification, Regex.Escape("approved by Marco 2026-10-29"))
                .Should().HaveCount(1, label);
            justification.Should().NotContain("PENDING", label);
        }

        entries.Where(e => Str(e, "issue") is "#142" or "#183").Should().BeEmpty();

        // The collector is bumped, so it carries no Go exception.
        entries.Where(e => Str(e, "image") == "otelcollector"
                        && (Str(e, "package") == "stdlib" || Str(e, "package").Contains('/')))
            .Should().BeEmpty();
    }

    [Fact]
    public void The_Keycloak_tag_is_an_exact_26_x_y_patch_and_the_registry_is_quay_io()
    {
        ContainerImages.KeycloakRegistry.Should().Be("quay.io");
        ContainerImages.KeycloakTag.Should().MatchRegex(@"^26\.\d+\.\d+$");
    }

    [Fact]
    public void The_Redis_tag_is_an_exact_8_x_y_patch_and_the_registry_is_docker_io()
    {
        ContainerImages.RedisRegistry.Should().Be("docker.io");
        ContainerImages.RedisImage.Should().Be("library/redis");
        ContainerImages.RedisTag.Should().MatchRegex(@"^8\.\d+\.\d+-alpine$");
    }

    [Fact]
    public void Every_pinned_digest_is_64_lowercase_hex_characters()
    {
        ContainerImages.PostgresSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        ContainerImages.KeycloakSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        ContainerImages.RedisSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        ContainerImages.CaddySha256.Should().MatchRegex("^[0-9a-f]{64}$");
        ContainerImages.OtelCollectorSha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void The_Postgres_constants_are_unchanged_from_the_existing_sandbox_line()
    {
        ContainerImages.PostgresRegistry.Should().Be("docker.io");
        ContainerImages.PostgresImage.Should().Be("library/postgres");
        ContainerImages.PostgresTag.Should().Be("18-alpine");
    }

    /// <summary>
    /// G4-17-18 (T-10): the Testcontainers fixture must reach the pinned image only through
    /// <see cref="KeycloakRealmFixture.BuildContainer"/>'s ordinary image pull, never a bind
    /// mount or the Docker socket, so it stays safe to run from the ADR-0010 sandbox sidecar
    /// (agent-sandbox-docker-sidecar.md, #41 T-41-16). Reads the fixture's own source, so it
    /// never needs Docker.
    /// </summary>
    [Fact]
    public void The_Keycloak_fixture_uses_no_bind_mount_and_never_mentions_the_Docker_socket()
    {
        var content = File.ReadAllText(
            RepoPaths.Find(Path.Combine("tests", "Decisya.Identity.Tests", "KeycloakRealmFixture.cs")));

        content.Should().NotContain("WithBindMount");
        content.Should().NotContain("docker.sock");
    }
}
