using Decisya.AppHost;
using Decisya.AppHost.Deploy;

var builder = DistributedApplication.CreateBuilder(args);

// Issue #120 (ADR-0018, G2 D1): the publish operation builds the deployable stack's model
// (Compose from `aspire publish`), never the dev model below. No dev secret, no dev realm.
if (builder.ExecutionContext.IsPublishMode)
{
    ComposeStack.Build(builder);
    builder.Build().Run();
    return;
}

// Set by Marco once (GETTING-STARTED §3). Fails fast, naming the key, never echoing the value.
RealmSecretRules.EnsureDevUserPassword(builder.Configuration["Parameters:dev-user-password"]);

var devUserPassword = builder.AddParameter("dev-user-password", secret: true);
var bffClientSecret = builder.AddParameter(
    "bff-client-secret",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true,
    persist: true);
var keycloakDbPassword = builder.AddParameter(
    "keycloak-db-password",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true,
    persist: true);

// Issue #21 (0.09 Modules.Tenancy, G2): the decisya_tenancy role's password. Generated,
// alphanumeric and at least 32 characters (G3 G4-21-05: the migrator validates the same
// shape before it ever uses the value). Never logged; carried to decisya-migrator as
// Migrator__TenancyRolePassword and to decisya-api only inside ConnectionStrings__tenancy.
var tenancyDbPassword = builder.AddParameter(
    "tenancy-db-password",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true,
    persist: true);

// Issue #23 (0.11 Modules.Entitlements, G2/G3 G4-23-04): the decisya_entitlements role's
// password, the same shape and handling as the tenancy one above. Carried to
// decisya-migrator as Migrator__EntitlementsRolePassword and to decisya-api only inside
// ConnectionStrings__entitlements.
var entitlementsDbPassword = builder.AddParameter(
    "entitlements-db-password",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true,
    persist: true);

// Issue #17 G4 fix: never hard-code Marco's dev volume name. A Category=AppHost test
// (Decisya.AppHost.Tests) passes a unique, throwaway name here instead and removes it
// afterwards, so a test AppHost can never attach to, and contend with, Marco's own
// running AppHost on "decisya-postgres-data" (two Postgres servers on the same data
// directory left Keycloak unable to become healthy, and a second run against the still-
// locked volume then hung).
const string DefaultPostgresDataVolumeName = "decisya-postgres-data";
var postgresDataVolumeName = builder.Configuration["Postgres:DataVolumeName"] ?? DefaultPostgresDataVolumeName;

// Marco's decision (2026-09-27, G4 #17): a real dev run keeps postgres and keycloak as
// persistent, fixed-name containers, so a later `dotnet run` reuses the already-running
// container instead of starting a second writer against the same data volume — an
// orphaned, non-persistent container left over from a hard AppHost stop otherwise keeps
// writing to the volume while a new start writes to it too, and that corrupted Marco's
// dev volume once already. A Category=AppHost test flips this off (Aspire's default
// Session lifetime, an auto-generated unique container name) so it can never attach to,
// or stop, Marco's own persistent dev containers.
var useEphemeralContainers = string.Equals(
    builder.Configuration["AppHost:UseEphemeralContainers"], "true", StringComparison.OrdinalIgnoreCase);

// G6-04: the one combination that reintroduces the 2026-09-26 incident (an orphaned,
// non-persistent container kept writing to "decisya-postgres-data" while a new start wrote
// to it too, corrupting it) is ephemeral mode against the default dev volume. Refuse it at
// start, with a clear message, rather than letting a mistyped or missing override attach a
// throwaway AppHost to Marco's real data. The name shape check matches exactly what
// TestAppHostIsolation.CreateVolumeName generates, so only that helper's own throwaway
// names are accepted in ephemeral mode.
if (useEphemeralContainers)
{
    if (string.Equals(postgresDataVolumeName, DefaultPostgresDataVolumeName, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "AppHost:UseEphemeralContainers=true must never run against the default Postgres data volume " +
            $"'{DefaultPostgresDataVolumeName}' (Marco's own dev data). Pass a distinct Postgres:DataVolumeName.");
    }

    if (!System.Text.RegularExpressions.Regex.IsMatch(postgresDataVolumeName, "^decisya-apphosttests-[0-9a-f]{32}$"))
    {
        throw new InvalidOperationException(
            "AppHost:UseEphemeralContainers=true requires Postgres:DataVolumeName to match " +
            $"'^decisya-apphosttests-[0-9a-f]{{32}}$' (TestAppHostIsolation's generated shape), not " +
            $"'{postgresDataVolumeName}'.");
    }
}

var postgres = builder.AddPostgres("postgres") // superuser password: Aspire-generated, persisted
    .WithImageRegistry(ContainerImages.PostgresRegistry)
    .WithImage(ContainerImages.PostgresImage, ContainerImages.PostgresTag)
    .WithImageSHA256(ContainerImages.PostgresSha256)
    .WithDataVolume(postgresDataVolumeName)
    .WithInitFiles(Path.Combine("..", "..", "deploy", "postgres", "init"))
    .WithEnvironment("DECISYA_KEYCLOAK_DB_PASSWORD", keycloakDbPassword);

if (!useEphemeralContainers)
{
    postgres.WithLifetime(ContainerLifetime.Persistent).WithContainerName("decisya-postgres");
}

var pg = postgres.GetEndpoint("tcp");

// Issue #21 (0.09 Modules.Tenancy, G2 D4): decisya-migrator owns all DDL for this database
// and provisions the least-privilege decisya_tenancy role over an owner connection
// (dev: postgres's own superuser, until 0.16). decisya-api never gets that owner
// connection (G3 G4-21-05, T-12): WithReference(decisyaDb) goes only on the migrator.
var decisyaDb = postgres.AddDatabase("decisya");

var migrator = builder.AddProject<Projects.Decisya_Infrastructure_Migrator>("decisya-migrator")
    .WithReference(decisyaDb)
    .WithEnvironment("Migrator__TenancyRolePassword", tenancyDbPassword)
    .WithEnvironment("Migrator__EntitlementsRolePassword", entitlementsDbPassword)
    .WaitFor(decisyaDb);

var keycloak = builder.AddKeycloak("keycloak", port: 8080) // admin password: Aspire-generated, persisted
    .WithImageRegistry(ContainerImages.KeycloakRegistry)
    .WithImage(ContainerImages.KeycloakImage, ContainerImages.KeycloakTag)
    .WithImageSHA256(ContainerImages.KeycloakSha256)
    .WithRealmImport(Path.Combine("..", "..", "deploy", "keycloak", "decisya-realm.json"))
    .WithEnvironment("KC_DB", "postgres")
    .WithEnvironment("KC_DB_URL", ReferenceExpression.Create(
        $"jdbc:postgresql://{pg.Property(EndpointProperty.Host)}:{pg.Property(EndpointProperty.TargetPort)}/keycloak"))
    .WithEnvironment("KC_DB_USERNAME", "keycloak")
    .WithEnvironment("KC_DB_PASSWORD", keycloakDbPassword)
    .WithEnvironment("DECISYA_BFF_CLIENT_SECRET", bffClientSecret)
    .WithEnvironment("DECISYA_DEV_USER_PASSWORD", devUserPassword)
    .WaitFor(postgres);

if (!useEphemeralContainers)
{
    keycloak.WithLifetime(ContainerLifetime.Persistent).WithContainerName("decisya-keycloak");
}

// Issue #18 (0.06 BFF): the first Redis resource, the BFF's session ticket store.
// Password: Aspire-generated (AddRedis with no explicit password parameter generates one),
// auth always on (ADR-0007). No data volume: sessions do not have to survive a Redis
// restart (D6, docs/architecture/bff-session.md).
var redis = builder.AddRedis("redis")
    .WithImageRegistry(ContainerImages.RedisRegistry)
    .WithImage(ContainerImages.RedisImage, ContainerImages.RedisTag)
    .WithImageSHA256(ContainerImages.RedisSha256);

if (!useEphemeralContainers)
{
    redis.WithLifetime(ContainerLifetime.Persistent).WithContainerName("decisya-redis");
}

// Issue #19 (0.07 YARP forwarding): the "https" profile is required so the BFF can resolve
// "https://decisya-api" through service discovery.
// Issue #20 (0.08 API JWT bearer validation): the authority is the same Keycloak "http"
// endpoint expression the BFF uses below, so the discovery issuer matches every token's
// "iss". Deliberately no .WithReference(keycloak): a service-discovery host would not match
// the token's iss, and the API is a bearer-only resource server that needs no secret (S-3).
// Issue #21 (0.09, G2): decisya-api connects only as the least-privilege decisya_tenancy
// role, over the host-published Postgres port (Port, not TargetPort: the API runs on the
// host, not in a container — G2's "Database, roles, migrator" section). It waits for
// decisya-migrator to finish creating the schema and the role before it ever opens that
// connection; G3 G4-21-05 requires the API's ConnectionStrings__* set to stay least-privilege only.
// Issue #23 (G3 G4-23-04): exactly two keys, tenancy and entitlements, each its own role.
var api = builder.AddProject<Projects.Decisya_Api>("decisya-api", launchProfileName: "https")
    .WithEnvironment("Api__Jwt__Authority", ReferenceExpression.Create(
        $"{keycloak.GetEndpoint("http").Property(EndpointProperty.Url)}/realms/decisya"))
    .WithEnvironment("ConnectionStrings__tenancy", ReferenceExpression.Create(
        $"Host={pg.Property(EndpointProperty.Host)};Port={pg.Property(EndpointProperty.Port)};Database=decisya;Username=decisya_tenancy;Password={tenancyDbPassword}"))
    .WithEnvironment("ConnectionStrings__entitlements", ReferenceExpression.Create(
        $"Host={pg.Property(EndpointProperty.Host)};Port={pg.Property(EndpointProperty.Port)};Database=decisya;Username=decisya_entitlements;Password={entitlementsDbPassword}"))
    .WaitFor(keycloak)
    .WaitForCompletion(migrator);

// Issue #18 (0.06 BFF): the authority comes from Keycloak's own primary ("http") endpoint,
// so its scheme follows Aspire's dev-cert termination and is never hard-coded (G2). The
// client secret reuses the existing "bff-client-secret" parameter Keycloak already imports.
builder.AddProject<Projects.Decisya_Bff>("decisya-bff", launchProfileName: "https")
    .WithReference(redis)
    .WithReference(api) // Issue #19: injects services__decisya-api__https__0 for service discovery
    .WithEnvironment("Bff__Oidc__Authority", ReferenceExpression.Create(
        $"{keycloak.GetEndpoint("http").Property(EndpointProperty.Url)}/realms/decisya"))
    .WithEnvironment("Bff__Oidc__ClientSecret", bffClientSecret)
    .WaitFor(redis)
    .WaitFor(keycloak);

builder.Build().Run();
