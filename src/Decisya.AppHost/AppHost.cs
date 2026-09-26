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

var postgres = builder.AddPostgres("postgres") // superuser password: Aspire-generated, persisted
    .WithImageRegistry(ContainerImages.PostgresRegistry)
    .WithImage(ContainerImages.PostgresImage, ContainerImages.PostgresTag)
    .WithImageSHA256(ContainerImages.PostgresSha256)
    .WithDataVolume("decisya-postgres-data")
    .WithInitFiles(Path.Combine("..", "..", "deploy", "postgres", "init"))
    .WithEnvironment("DECISYA_KEYCLOAK_DB_PASSWORD", keycloakDbPassword);

var pg = postgres.GetEndpoint("tcp");

builder.AddKeycloak("keycloak", port: 8080) // admin password: Aspire-generated, persisted
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

builder.AddProject<Projects.Decisya_Api>("decisya-api"); // unchanged; #20 adds .WithReference(keycloak)

builder.Build().Run();
