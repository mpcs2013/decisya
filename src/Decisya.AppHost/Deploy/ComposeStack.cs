using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace Decisya.AppHost.Deploy;

/// <summary>
/// The publish-mode model for the deployable stack (issue #120, ADR-0018, G2 D1/D2/D6). Selected
/// by <c>builder.ExecutionContext.IsPublishMode</c> at the top of <c>AppHost.cs</c>; the dev
/// (run-mode) model is untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this file owns</b> (D1 key ownership): services, images, non-secret environment,
/// <c>depends_on</c>, healthchecks, and the entrypoint or command of the images that need one.
/// <b>What it never sets</b>: <c>networks</c>, <c>ports</c>, <c>volumes</c>, <c>secrets</c>,
/// <c>configs</c>, <c>cap_drop</c>, <c>security_opt</c>, <c>user</c>, <c>read_only</c>, limits,
/// <c>logging</c> and <c>restart</c>. Those belong to the hand-written overlay
/// (<c>deploy/compose/docker-compose.stack.yaml</c>).
/// </para>
/// <para>
/// <b>Secrets (G4-120-01):</b> there is no <c>AddParameter</c> here at all, so no secret
/// parameter can reach a container environment. Credentials arrive only as <c>*_FILE</c>
/// variables that point into <c>/run/secrets</c>, or as files the key-per-file configuration
/// source reads there. Plain <c>AddContainer</c> is used instead of the typed Postgres, Redis and
/// Keycloak integrations because those wire generated secret parameters into the environment.
/// </para>
/// <para>
/// <b>Addresses:</b> hosts, the HTTPS port and the two allow-list values are
/// <c>${DECISYA_*}</c> placeholders that Compose fills from the stack's <c>.env</c>. The only
/// address literals are Caddy's two fixed addresses on the committed Docker subnets
/// (10.120.0.0/24 and 10.120.1.0/24) and loopback.
/// </para>
/// </remarks>
internal static class ComposeStack
{
    /// <summary>Caddy's fixed address on the internal <c>backchannel</c> network (the BFF and Api's single trusted proxy).</summary>
    internal const string BackchannelProxyAddress = "10.120.0.2";

    /// <summary>Caddy's fixed address on the internal <c>idp</c> network (Keycloak's single trusted proxy).</summary>
    internal const string IdpProxyAddress = "10.120.1.2";

    /// <summary>The release image placeholders. Their values come from the stack's <c>.env</c>.</summary>
    internal const string ApiImagePlaceholder = "${DECISYA_API_IMAGE}";
    internal const string BffImagePlaceholder = "${DECISYA_BFF_IMAGE}";
    internal const string MigratorImagePlaceholder = "${DECISYA_MIGRATOR_IMAGE}";

    /// <summary>Where the overlay mounts Caddy's exported public root (read-only).</summary>
    internal const string TrustedRootPath = "/etc/decisya/trust/caddy-root.crt";

    /// <summary>Where the overlay mounts the Keycloak wrapper (run through <c>/bin/sh</c>, so no exec bit is needed).</summary>
    internal const string KeycloakEntrypointPath = "/opt/decisya/entrypoint-stack.sh";

    /// <summary>The BFF's Data Protection key ring directory (the overlay mounts the <c>bff-keyring</c> volume here).</summary>
    internal const string KeyRingPath = "/home/app/keyring";

    internal const string OtlpEndpoint = "http://otel-collector:4317";

    internal const string ConditionHealthy = "service_healthy";
    internal const string ConditionStarted = "service_started";
    internal const string ConditionCompleted = "service_completed_successfully";

    /// <summary>The environment keys the stack's <c>.env</c> supplies to the generated file.</summary>
    internal static readonly string[] EnvironmentKeys =
    [
        "DECISYA_API_HOST",
        "DECISYA_API_IMAGE",
        "DECISYA_APP_HOST",
        "DECISYA_BFF_IMAGE",
        "DECISYA_HTTPS_PORT",
        "DECISYA_ID_HOST",
        "DECISYA_LAN_SUBNET",
        "DECISYA_MIGRATOR_IMAGE",
        "DECISYA_WORKSTATION_ADDRESS",
    ];

    internal static void Build(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The Aspire dashboard is an unpinned image with a published port and OTLP wiring.
        builder.AddDockerComposeEnvironment("decisya").WithDashboard(false);

        AddThirdParty(
            builder, "postgres",
            ContainerImages.PostgresRegistry, ContainerImages.PostgresImage, ContainerImages.PostgresTag, ContainerImages.PostgresSha256,
            service =>
            {
                // Native *_FILE support. The init scripts (deploy/postgres/init) read the other two files;
                // DECISYA_STACK=1 makes a missing migrator password fail the first init loudly (C-06).
                service.Environment["POSTGRES_PASSWORD_FILE"] = "/run/secrets/postgres_superuser_password";
                service.Environment["DECISYA_STACK"] = "1";
                service.Environment["DECISYA_MIGRATOR_DB_PASSWORD_FILE"] = "/run/secrets/migrator_db_password";
                service.Environment["DECISYA_KEYCLOAK_DB_PASSWORD_FILE"] = "/run/secrets/keycloak_db_password";
                service.Healthcheck = Probe(["CMD-SHELL", "pg_isready -U postgres -d postgres"], startPeriod: "20s");
            });

        AddThirdParty(
            builder, "redis",
            ContainerImages.RedisRegistry, ContainerImages.RedisImage, ContainerImages.RedisTag, ContainerImages.RedisSha256,
            service =>
            {
                // The overlay mounts deploy/redis/redis.conf here (aclfile, maxmemory, no persistence).
                service.Command = ["redis-server", "/usr/local/etc/redis/redis.conf"];

                // Password-free: the `health` ACL user is `nopass` with `+ping` only (G4-120-01), so no
                // credential reaches argv or `docker inspect`. redis-cli only sends AUTH when it is given
                // a password, hence the explicit empty one.
                service.Healthcheck = Probe(
                    ["CMD", "redis-cli", "--user", "health", "--no-auth-warning", "-a", "", "ping"],
                    startPeriod: "5s");
            });

        AddThirdParty(
            builder, "keycloak",
            ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage, ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256,
            service =>
            {
                // deploy/keycloak/entrypoint-stack.sh reads the *_FILE variables, exports the passwords
                // into the Keycloak process only, and execs `kc.sh start` (never start-dev, no realm
                // import: the Phase 0 realm is #121).
                service.Entrypoint = ["/bin/sh", KeycloakEntrypointPath];
                service.Environment["KC_DB"] = "postgres";
                service.Environment["KC_DB_URL"] = "jdbc:postgresql://postgres:5432/keycloak";
                service.Environment["KC_DB_USERNAME"] = "keycloak";
                service.Environment["KC_DB_PASSWORD_FILE"] = "/run/secrets/keycloak_db_password";
                service.Environment["KC_BOOTSTRAP_ADMIN_USERNAME"] = "decisya-bootstrap";
                service.Environment["KC_BOOTSTRAP_ADMIN_PASSWORD_FILE"] = "/run/secrets/keycloak_bootstrap_admin_password";
                service.Environment["KC_HOSTNAME"] = "https://${DECISYA_ID_HOST}:${DECISYA_HTTPS_PORT}";
                service.Environment["KC_HTTP_ENABLED"] = "true";
                service.Environment["KC_PROXY_HEADERS"] = "xforwarded";
                service.Environment["KC_PROXY_TRUSTED_ADDRESSES"] = IdpProxyAddress;
                service.Environment["KC_HEALTH_ENABLED"] = "true";
                service.Environment["KC_TRUSTSTORE_PATHS"] = TrustedRootPath;

                // Keycloak's image has bash and no curl. The management port 9000 answers only once the
                // server is up, so a successful TCP connect and request write on loopback is the probe
                // (Keycloak's documented healthcheck). 9000 is never routed by Caddy.
                service.Healthcheck = Probe(
                [
                    "CMD",
                    "bash",
                    "-c",
                    "exec 3<>/dev/tcp/127.0.0.1/9000 && echo -e 'GET /health/ready HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3",
                ],
                startPeriod: "90s",
                retries: 15);

                service.DependsOn["postgres"] = Dependency(ConditionHealthy);
            });

        AddThirdParty(
            builder, "caddy",
            ContainerImages.CaddyRegistry, ContainerImages.CaddyImage, ContainerImages.CaddyTag, ContainerImages.CaddySha256,
            service =>
            {
                // The Caddyfile reads these as {$VAR}; the overlay mounts it at /etc/caddy/Caddyfile
                // (the image default command) and publishes the one port.
                service.Environment["DECISYA_APP_HOST"] = "${DECISYA_APP_HOST}";
                service.Environment["DECISYA_ID_HOST"] = "${DECISYA_ID_HOST}";
                service.Environment["DECISYA_API_HOST"] = "${DECISYA_API_HOST}";
                service.Environment["DECISYA_LAN_SUBNET"] = "${DECISYA_LAN_SUBNET}";
                service.Environment["DECISYA_WORKSTATION_ADDRESS"] = "${DECISYA_WORKSTATION_ADDRESS}";

                // `admin off` leaves no admin API to probe, so check that the TLS listener accepts.
                service.Healthcheck = Probe(["CMD", "nc", "-z", "127.0.0.1", "8443"], startPeriod: "10s");
            });

        // The core collector image is distroless: no shell, no probe binary, so no healthcheck.
        // Dependents use service_started. The overlay mounts config.yaml at /etc/otelcol/config.yaml
        // (the image default --config path).
        AddThirdParty(
            builder, "otel-collector",
            ContainerImages.OtelCollectorRegistry, ContainerImages.OtelCollectorImage,
            ContainerImages.OtelCollectorTag, ContainerImages.OtelCollectorSha256,
            _ => { });

        AddRelease(
            builder, "migrator", "decisya-migrator", MigratorImagePlaceholder,
            service =>
            {
                service.Environment["DOTNET_ENVIRONMENT"] = "Production";
                service.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = OtlpEndpoint;
                service.DependsOn["postgres"] = Dependency(ConditionHealthy);
                service.DependsOn["otel-collector"] = Dependency(ConditionStarted);
            });

        AddRelease(
            builder, "api", "decisya-api", ApiImagePlaceholder,
            service =>
            {
                ConfigureWebHost(service, allowedHosts: "${DECISYA_API_HOST}");
                service.Environment["Api__Jwt__Authority"] = "https://${DECISYA_ID_HOST}:${DECISYA_HTTPS_PORT}/realms/decisya";
                service.Environment["Api__Jwt__TrustedRootPath"] = TrustedRootPath;
                service.Healthcheck = Probe(["CMD", "dotnet", "/app/Decisya.Api.dll", "--health-probe"], startPeriod: "20s");
                service.DependsOn["migrator"] = Dependency(ConditionCompleted);
                service.DependsOn["caddy"] = Dependency(ConditionHealthy);
                service.DependsOn["otel-collector"] = Dependency(ConditionStarted);
            });

        AddRelease(
            builder, "bff", "decisya-bff", BffImagePlaceholder,
            service =>
            {
                ConfigureWebHost(service, allowedHosts: "${DECISYA_APP_HOST}");
                service.Environment["Bff__Oidc__Authority"] = "https://${DECISYA_ID_HOST}:${DECISYA_HTTPS_PORT}/realms/decisya";
                service.Environment["Bff__Api__Address"] = "https://${DECISYA_API_HOST}:${DECISYA_HTTPS_PORT}";
                service.Environment["Bff__Backchannel__TrustedRootPath"] = TrustedRootPath;
                service.Environment["Bff__DataProtection__KeyRingPath"] = KeyRingPath;
                service.Healthcheck = Probe(["CMD", "dotnet", "/app/Decisya.Bff.dll", "--health-probe"], startPeriod: "20s");
                service.DependsOn["redis"] = Dependency(ConditionHealthy);
                service.DependsOn["caddy"] = Dependency(ConditionHealthy);
                service.DependsOn["api"] = Dependency(ConditionHealthy);
                service.DependsOn["otel-collector"] = Dependency(ConditionStarted);
            });
    }

    private static void ConfigureWebHost(Service service, string allowedHosts)
    {
        service.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        // 8080 serves traffic, 8081 is the management listener (health). Caddy routes only 8080, and
        // Compose never publishes either.
        service.Environment["ASPNETCORE_HTTP_PORTS"] = "8080;8081";
        service.Environment["AllowedHosts"] = allowedHosts;
        // Exactly Caddy's fixed address on the backchannel network (G2 D2).
        service.Environment["Decisya__Edge__TrustedProxies"] = BackchannelProxyAddress;
        service.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = OtlpEndpoint;
    }

    private static void AddThirdParty(
        IDistributedApplicationBuilder builder, string name,
        string registry, string image, string tag, string sha256,
        Action<Service> configure)
    {
        builder.AddContainer(name, image)
            .WithImage(image, tag)
            .WithImageRegistry(registry)
            .WithImageSHA256(sha256)
            .PublishAsDockerComposeService((_, service) =>
            {
                service.Image = ContainerImages.Reference(registry, image, tag, sha256);
                configure(service);
            });
    }

    private static void AddRelease(
        IDistributedApplicationBuilder builder, string name, string repository, string placeholder,
        Action<Service> configure)
    {
        builder.AddContainer(name, repository)
            .PublishAsDockerComposeService((_, service) =>
            {
                service.Image = placeholder;
                configure(service);
            });
    }

    private static Healthcheck Probe(List<string> test, string startPeriod, int retries = 10) => new()
    {
        Test = test,
        Interval = "10s",
        Timeout = "5s",
        StartPeriod = startPeriod,
        Retries = retries,
    };

    private static ServiceDependency Dependency(string condition) => new() { Condition = condition };
}
