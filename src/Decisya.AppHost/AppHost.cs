using Decisya.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

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

// Issue #17 G4 fix: never hard-code Marco's dev volume name. A Category=AppHost test
// (Decisya.AppHost.Tests) passes a unique, throwaway name here instead and removes it
// afterwards, so a test AppHost can never attach to, and contend with, Marco's own
// running AppHost on "decisya-postgres-data" (two Postgres servers on the same data
// directory left Keycloak unable to become healthy, and a second run against the still-
// locked volume then hung).
var postgresDataVolumeName = builder.Configuration["Postgres:DataVolumeName"] ?? "decisya-postgres-data";

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

builder.AddProject<Projects.Decisya_Api>("decisya-api"); // unchanged; #20 adds .WithReference(keycloak)

builder.Build().Run();
