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
    public void The_Keycloak_tag_is_an_exact_26_x_y_patch_and_the_registry_is_quay_io()
    {
        ContainerImages.KeycloakRegistry.Should().Be("quay.io");
        ContainerImages.KeycloakTag.Should().MatchRegex(@"^26\.\d+\.\d+$");
    }

    [Fact]
    public void Every_pinned_digest_is_64_lowercase_hex_characters()
    {
        ContainerImages.PostgresSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        ContainerImages.KeycloakSha256.Should().MatchRegex("^[0-9a-f]{64}$");
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
