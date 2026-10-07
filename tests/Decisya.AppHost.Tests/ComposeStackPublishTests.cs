using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Decisya.AppHost.Deploy;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Issue #120 (G4-120-01, G4-120-03, ADR-0018 D1): the publish-mode model. It is built with
/// <c>--operation publish</c> and <see cref="ComposeStack.Build"/> only, and published with
/// <c>--step publish</c>, which writes just <c>docker-compose.yaml</c>: no Docker daemon, no DCP,
/// no container, no image build. So these tests are safe next to Marco's dev AppHost.
/// </summary>
/// <remarks>
/// The generated file is asserted as text, service block by service block, because that is what
/// the deploy guards (<c>deploy/tests</c>) and the operator read. Failure messages never include
/// a value from the file: it holds placeholders and non-secret names only, but the habit is kept.
/// </remarks>
[Trait("Category", "AppHost")]
public partial class ComposeStackPublishTests
{
    private static readonly string[] ExpectedServices =
        ["api", "bff", "caddy", "keycloak", "migrator", "otel-collector", "postgres", "redis"];

    private static readonly Lazy<Task<string>> PublishedYaml = new(PublishAsync);

    // Compose keys the hand-written overlay owns (D1). The generated file must never carry one.
    // `networks` is excluded on purpose: the publisher adds its default `aspire` network, which
    // the overlay replaces with !override.
    private static readonly string[] OverlayOwnedKeys =
    [
        "ports", "volumes", "secrets", "configs", "cap_drop", "cap_add", "security_opt", "user",
        "read_only", "tmpfs", "mem_limit", "cpus", "pids_limit", "logging", "restart", "privileged",
        "devices", "network_mode", "pid", "env_file", "build", "extends", "include",
    ];

    // The only address literals that may appear: Caddy's two fixed addresses on the committed
    // Docker subnets, and loopback for the in-container probes.
    private static readonly string[] AllowedAddressLiterals =
        [ComposeStack.BackchannelProxyAddress, ComposeStack.IdpProxyAddress, "127.0.0.1"];

    private static IDistributedApplicationBuilder CreateModel() =>
        BuildModel(DistributedApplication.CreateBuilder(["--operation", "publish"]));

    private static IDistributedApplicationBuilder BuildModel(IDistributedApplicationBuilder builder)
    {
        ComposeStack.Build(builder);
        return builder;
    }

    private static async Task<string> PublishAsync()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "decisya-compose-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputPath);
        try
        {
            var builder = DistributedApplication.CreateBuilder(
                ["--operation", "publish", "--step", "publish", "--output-path", outputPath]);
            ComposeStack.Build(builder);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var app = builder.Build();
            await app.RunAsync(timeout.Token);

            return await File.ReadAllTextAsync(Path.Combine(outputPath, "docker-compose.yaml"), CancellationToken.None);
        }
        finally
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    // ---- model-level tests (no publish) ----

    [Fact]
    public void The_publish_model_has_no_parameter_at_all_so_no_secret_parameter()
    {
        var model = CreateModel();

        model.Resources.OfType<ParameterResource>().Should().BeEmpty(
            "G4-120-01: no parameter, and so no secret parameter, can reach a container environment");
    }

    [Fact]
    public void The_publish_model_holds_exactly_the_expected_services_and_no_dashboard()
    {
        var model = CreateModel();

        model.Resources.OfType<ContainerResource>().Select(r => r.Name)
            .Should().BeEquivalentTo(ExpectedServices);
        model.Resources.Select(r => r.Name).Should().NotContain(
            name => name.Contains("dashboard", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("caddy", ContainerImages.CaddyRegistry, ContainerImages.CaddyImage, ContainerImages.CaddyTag, ContainerImages.CaddySha256)]
    [InlineData("otel-collector", ContainerImages.OtelCollectorRegistry, ContainerImages.OtelCollectorImage, ContainerImages.OtelCollectorTag, ContainerImages.OtelCollectorSha256)]
    [InlineData("postgres", ContainerImages.PostgresRegistry, ContainerImages.PostgresImage, ContainerImages.PostgresTag, ContainerImages.PostgresSha256)]
    [InlineData("redis", ContainerImages.RedisRegistry, ContainerImages.RedisImage, ContainerImages.RedisTag, ContainerImages.RedisSha256)]
    [InlineData("keycloak", ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage, ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256)]
    public void Third_party_services_carry_the_ContainerImages_pin(
        string name, string registry, string image, string tag, string sha256)
    {
        var model = CreateModel();

        var annotation = model.Resources.Single(r => r.Name == name)
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        annotation.Registry.Should().Be(registry);
        annotation.Image.Should().Be(image);
        // WithImageSHA256 clears the tag (a digest replaces it). The published image line carries tag@digest,
        // which the generated-file test below asserts with ContainerImages.Reference (main session, G4 re-run).
        annotation.Tag.Should().BeNull();
        annotation.SHA256.Should().Be(sha256);
        tag.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("api")]
    [InlineData("bff")]
    [InlineData("migrator")]
    public void Release_services_carry_no_pin_in_the_model_because_the_image_is_a_placeholder(string name)
    {
        var model = CreateModel();

        var annotation = model.Resources.Single(r => r.Name == name)
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        annotation.SHA256.Should().BeNull();
        annotation.Registry.Should().BeNull();
    }

    // ---- generated-file tests ----

    [Fact]
    public async Task The_generated_file_has_exactly_the_expected_services()
    {
        var yaml = await PublishedYaml.Value;

        ServiceNames(yaml).Should().BeEquivalentTo(ExpectedServices);
    }

    [Theory]
    [InlineData("caddy", ContainerImages.CaddyRegistry, ContainerImages.CaddyImage, ContainerImages.CaddyTag, ContainerImages.CaddySha256)]
    [InlineData("otel-collector", ContainerImages.OtelCollectorRegistry, ContainerImages.OtelCollectorImage, ContainerImages.OtelCollectorTag, ContainerImages.OtelCollectorSha256)]
    [InlineData("postgres", ContainerImages.PostgresRegistry, ContainerImages.PostgresImage, ContainerImages.PostgresTag, ContainerImages.PostgresSha256)]
    [InlineData("redis", ContainerImages.RedisRegistry, ContainerImages.RedisImage, ContainerImages.RedisTag, ContainerImages.RedisSha256)]
    [InlineData("keycloak", ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage, ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256)]
    public async Task The_generated_file_pins_each_third_party_image_by_tag_and_digest(
        string name, string registry, string image, string tag, string sha256)
    {
        var yaml = await PublishedYaml.Value;

        Scalar(ServiceBlock(yaml, name), "image")
            .Should().Be(ContainerImages.Reference(registry, image, tag, sha256));
    }

    [Theory]
    [InlineData("api", "${DECISYA_API_IMAGE}")]
    [InlineData("bff", "${DECISYA_BFF_IMAGE}")]
    [InlineData("migrator", "${DECISYA_MIGRATOR_IMAGE}")]
    public async Task The_generated_file_uses_a_placeholder_for_each_release_image(string name, string placeholder)
    {
        var yaml = await PublishedYaml.Value;

        Scalar(ServiceBlock(yaml, name), "image").Should().Be(placeholder);
    }

    [Fact]
    public async Task The_generated_file_has_no_dashboard_and_no_Aspire_dashboard_or_OTLP_header_variable()
    {
        var yaml = await PublishedYaml.Value;

        yaml.Should().NotContainEquivalentOf("dashboard");
        yaml.Should().NotContain("ASPIRE_");
        yaml.Should().NotContain("OTEL_EXPORTER_OTLP_HEADERS");
    }

    [Fact]
    public async Task The_generated_file_never_sets_the_management_port_test_seam()
    {
        var yaml = await PublishedYaml.Value;

        yaml.Should().NotContain("Decisya__Management__Port");
        yaml.Should().NotContain("Decisya:Management:Port");
    }

    [Fact]
    public async Task The_generated_file_holds_no_address_literal_beyond_the_two_fixed_Caddy_addresses_and_loopback()
    {
        var yaml = await PublishedYaml.Value;

        var literals = Ipv4Literal().Matches(yaml).Select(m => m.Value).Distinct().ToList();

        literals.Should().BeSubsetOf(AllowedAddressLiterals);
        // The two trusted-proxy keys must really be present, or the allow-list above is vacuous.
        literals.Should().Contain(ComposeStack.BackchannelProxyAddress);
        literals.Should().Contain(ComposeStack.IdpProxyAddress);
        Ipv6Literal().IsMatch(yaml).Should().BeFalse("no IPv6 address literal is committed");
    }

    [Fact]
    public async Task The_generated_file_carries_no_key_the_overlay_owns()
    {
        var yaml = await PublishedYaml.Value;

        foreach (var name in ExpectedServices)
        {
            var keys = TopLevelKeys(ServiceBlock(yaml, name));
            keys.Intersect(OverlayOwnedKeys).Should().BeEmpty($"service {name} must leave overlay-owned keys to the overlay");
        }

        TopLevelDocumentKeys(yaml).Intersect(["secrets", "configs", "volumes"]).Should().BeEmpty();
    }

    [Fact]
    public async Task No_environment_name_that_looks_like_a_credential_carries_anything_but_a_secret_file_path()
    {
        var yaml = await PublishedYaml.Value;
        var credentialName = CredentialName();

        foreach (var name in ExpectedServices)
        {
            foreach (var (key, value) in Environment(ServiceBlock(yaml, name)))
            {
                if (credentialName.IsMatch(key))
                {
                    key.Should().EndWith("_FILE", $"{name}: only a *_FILE variable may name a credential");
                    value.Should().StartWith("/run/secrets/", $"{name}: {key} must point into /run/secrets");
                }

                value.Should().NotMatchRegex("(?i)password\\s*=", $"{name}: {key}");
                value.Should().NotMatchRegex(@"://[^/@\s]+:[^/@\s]+@", $"{name}: {key} must not carry URL user info");
            }
        }

        yaml.Should().NotContain("POSTGRES_HOST_AUTH_METHOD");
        yaml.Should().NotMatchRegex("(?i)Include Error Detail|Persist Security Info");
    }

    [Fact]
    public async Task The_web_hosts_are_pinned_to_Production_with_two_listeners_and_a_trusted_proxy()
    {
        var yaml = await PublishedYaml.Value;

        foreach (var (name, allowedHosts) in new[] { ("api", "${DECISYA_API_HOST}"), ("bff", "${DECISYA_APP_HOST}") })
        {
            var environment = Environment(ServiceBlock(yaml, name));

            environment["ASPNETCORE_ENVIRONMENT"].Should().Be("Production");
            environment["ASPNETCORE_HTTP_PORTS"].Should().Be("8080;8081");
            environment["AllowedHosts"].Should().Be(allowedHosts);
            environment["Decisya__Edge__TrustedProxies"].Should().Be(ComposeStack.BackchannelProxyAddress);
            environment["OTEL_EXPORTER_OTLP_ENDPOINT"].Should().Be(ComposeStack.OtlpEndpoint);
            environment.Values.Should().NotContain(v => v == "*");
        }

        Environment(ServiceBlock(yaml, "migrator"))["DOTNET_ENVIRONMENT"].Should().Be("Production");
    }

    [Fact]
    public async Task The_back_channel_addresses_are_https_through_the_placeholders_and_trust_the_mounted_root()
    {
        var yaml = await PublishedYaml.Value;
        var api = Environment(ServiceBlock(yaml, "api"));
        var bff = Environment(ServiceBlock(yaml, "bff"));

        const string issuer = "https://${DECISYA_ID_HOST}:${DECISYA_HTTPS_PORT}/realms/decisya";
        api["Api__Jwt__Authority"].Should().Be(issuer);
        bff["Bff__Oidc__Authority"].Should().Be(issuer);
        bff["Bff__Api__Address"].Should().Be("https://${DECISYA_API_HOST}:${DECISYA_HTTPS_PORT}");
        api["Api__Jwt__TrustedRootPath"].Should().Be(ComposeStack.TrustedRootPath);
        bff["Bff__Backchannel__TrustedRootPath"].Should().Be(ComposeStack.TrustedRootPath);
    }

    [Fact]
    public async Task Keycloak_starts_through_the_wrapper_with_file_variables_and_never_start_dev_or_a_realm_import()
    {
        var yaml = await PublishedYaml.Value;
        var block = ServiceBlock(yaml, "keycloak");
        var environment = Environment(block);

        environment.Should().NotContainKey("KC_DB_PASSWORD");
        environment.Should().NotContainKey("KC_BOOTSTRAP_ADMIN_PASSWORD");
        environment["KC_DB_PASSWORD_FILE"].Should().Be("/run/secrets/keycloak_db_password");
        environment["KC_BOOTSTRAP_ADMIN_PASSWORD_FILE"].Should().Be("/run/secrets/keycloak_bootstrap_admin_password");
        environment["KC_HOSTNAME"].Should().Be("https://${DECISYA_ID_HOST}:${DECISYA_HTTPS_PORT}");
        environment["KC_PROXY_TRUSTED_ADDRESSES"].Should().Be(ComposeStack.IdpProxyAddress);
        environment["KC_TRUSTSTORE_PATHS"].Should().Be(ComposeStack.TrustedRootPath);

        var text = Text(block);
        text.Should().Contain("entrypoint-stack.sh");
        text.Should().NotContain("start-dev");
        text.Should().NotContain("--import-realm");
        yaml.Should().NotContain("decisya-realm");
    }

    [Fact]
    public async Task Postgres_reads_its_passwords_from_files_and_fails_the_init_without_the_migrator_password()
    {
        var yaml = await PublishedYaml.Value;
        var environment = Environment(ServiceBlock(yaml, "postgres"));

        environment["POSTGRES_PASSWORD_FILE"].Should().Be("/run/secrets/postgres_superuser_password");
        environment["DECISYA_STACK"].Should().Be("1");
        environment["DECISYA_MIGRATOR_DB_PASSWORD_FILE"].Should().Be("/run/secrets/migrator_db_password");
        environment["DECISYA_KEYCLOAK_DB_PASSWORD_FILE"].Should().Be("/run/secrets/keycloak_db_password");
        environment.Keys.Should().NotContain(["POSTGRES_PASSWORD", "POSTGRES_HOST_AUTH_METHOD"]);
    }

    [Fact]
    public async Task Healthchecks_use_the_probe_argument_or_a_credential_free_command()
    {
        var yaml = await PublishedYaml.Value;

        foreach (var name in new[] { "api", "bff" })
        {
            Text(ServiceBlock(yaml, name)).Should().Contain("--health-probe");
        }

        var redis = Text(ServiceBlock(yaml, "redis"));
        redis.Should().Contain("health");
        redis.Should().NotContainEquivalentOf("requirepass");
        redis.Should().NotContain("REDISCLI_AUTH");
    }

    [Fact]
    public async Task Start_order_waits_for_health_the_migrator_and_the_collector_as_designed()
    {
        var yaml = await PublishedYaml.Value;

        DependsOn(ServiceBlock(yaml, "api")).Should().Contain(new KeyValuePair<string, string>("migrator", "service_completed_successfully"))
            .And.Contain(new KeyValuePair<string, string>("caddy", "service_healthy"));
        DependsOn(ServiceBlock(yaml, "bff")).Should().Contain(new KeyValuePair<string, string>("redis", "service_healthy"))
            .And.Contain(new KeyValuePair<string, string>("api", "service_healthy"));
        DependsOn(ServiceBlock(yaml, "migrator")).Should().Contain(new KeyValuePair<string, string>("postgres", "service_healthy"));
        DependsOn(ServiceBlock(yaml, "keycloak")).Should().Contain(new KeyValuePair<string, string>("postgres", "service_healthy"));
    }

    // ---- minimal YAML reading: just enough for the generated Compose layout ----

    private static List<string> ServiceNames(string yaml)
    {
        var names = new List<string>();
        var inServices = false;
        foreach (var line in Lines(yaml))
        {
            if (line.Length > 0 && line[0] != ' ' && line[0] != '#')
            {
                inServices = line.TrimEnd() == "services:";
                continue;
            }

            var match = ServiceHeader().Match(line);
            if (inServices && match.Success)
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    private static List<string> ServiceBlock(string yaml, string name)
    {
        var block = new List<string>();
        var inServices = false;
        var inBlock = false;
        foreach (var line in Lines(yaml))
        {
            if (line.Length > 0 && line[0] != ' ' && line[0] != '#')
            {
                inServices = line.TrimEnd() == "services:";
                inBlock = false;
                continue;
            }

            var header = ServiceHeader().Match(line);
            if (inServices && header.Success)
            {
                inBlock = header.Groups["name"].Value == name;
                continue;
            }

            if (inServices && inBlock)
            {
                block.Add(line);
            }
        }

        block.Should().NotBeEmpty($"the generated file must contain service {name}");
        return block;
    }

    private static IEnumerable<string> TopLevelDocumentKeys(string yaml) =>
        Lines(yaml).Where(l => l.Length > 0 && l[0] != ' ' && l[0] != '#' && l.Contains(':'))
            .Select(l => l[..l.IndexOf(':')]);

    private static HashSet<string> TopLevelKeys(List<string> block) =>
        block.Select(l => ServiceKey().Match(l)).Where(m => m.Success).Select(m => m.Groups["key"].Value).ToHashSet();

    private static string Scalar(List<string> block, string key)
    {
        var line = block.Select(l => ServiceKey().Match(l)).FirstOrDefault(m => m.Success && m.Groups["key"].Value == key);
        line.Should().NotBeNull($"the service must have a {key} key");
        return Unquote(line!.Groups["rest"].Value);
    }

    private static Dictionary<string, string> Environment(List<string> block)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var inEnvironment = false;
        foreach (var line in block)
        {
            if (ServiceKey().IsMatch(line))
            {
                inEnvironment = ServiceKey().Match(line).Groups["key"].Value == "environment";
                continue;
            }

            if (!inEnvironment)
            {
                continue;
            }

            var entry = NestedEntry().Match(line);
            if (entry.Success)
            {
                result[Unquote(entry.Groups["key"].Value)] = Unquote(entry.Groups["rest"].Value);
            }
        }

        return result;
    }

    private static Dictionary<string, string> DependsOn(List<string> block)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var inDependsOn = false;
        string? current = null;
        foreach (var line in block)
        {
            var top = ServiceKey().Match(line);
            if (top.Success)
            {
                inDependsOn = top.Groups["key"].Value == "depends_on";
                current = null;
                continue;
            }

            if (!inDependsOn)
            {
                continue;
            }

            var deep = DeepEntry().Match(line);
            if (deep.Success)
            {
                if (deep.Groups["key"].Value == "condition" && current is not null)
                {
                    result[current] = Unquote(deep.Groups["rest"].Value);
                }

                continue;
            }

            var entry = NestedEntry().Match(line);
            if (entry.Success && entry.Groups["rest"].Value.Length == 0)
            {
                current = Unquote(entry.Groups["key"].Value);
            }
        }

        return result;
    }

    private static string Text(List<string> block) => string.Join('\n', block);

    private static string[] Lines(string yaml) =>
        yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : value;
    }

    [GeneratedRegex(@"^  (?<name>[A-Za-z0-9._-]+):\s*$")]
    private static partial Regex ServiceHeader();

    [GeneratedRegex(@"^    (?<key>[A-Za-z0-9_.-]+):\s*(?<rest>.*)$")]
    private static partial Regex ServiceKey();

    [GeneratedRegex("^      (?<key>\"[^\"]+\"|[A-Za-z0-9_.-]+):\\s*(?<rest>.*)$")]
    private static partial Regex NestedEntry();

    [GeneratedRegex(@"^        (?<key>[A-Za-z0-9_.-]+):\s*(?<rest>.*)$")]
    private static partial Regex DeepEntry();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex Ipv4Literal();

    [GeneratedRegex(@"(?i)(?<![0-9a-z])(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{0,4}(?![0-9a-z])")]
    private static partial Regex Ipv6Literal();

    [GeneratedRegex("(?i)PASSWORD|SECRET|TOKEN|CONNECTIONSTRING|OTLP_HEADERS|APIKEY|API_KEY")]
    private static partial Regex CredentialName();
}
