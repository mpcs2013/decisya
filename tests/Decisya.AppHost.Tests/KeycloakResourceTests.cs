using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Issue #17 (0.05): starts the real AppHost and proves the Postgres/Keycloak resource
/// wiring an operator would see on the dashboard — the Done-when (Story 6) and NFR-16.
/// Needs DCP, the Aspire CLI bundle and a running Docker daemon, so it runs on Marco's
/// host only (ADR-0010), like every other class in this project.
/// </summary>
[Trait("Category", "AppHost")]
public class KeycloakResourceTests
{
    private static readonly string[] NonSecretEnvironmentKeys = ["KC_DB", "KC_DB_USERNAME", "KC_DB_URL"];

    private static readonly string[] SecretEnvironmentKeys =
        ["KC_DB_PASSWORD", "DECISYA_BFF_CLIENT_SECRET", "DECISYA_DEV_USER_PASSWORD"];

    [Fact]
    public async Task Keycloak_and_Postgres_reach_healthy_and_the_login_page_is_reachable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Requires Parameters:dev-user-password in the AppHost's own user-secrets
        // (GETTING-STARTED §3); otherwise RealmSecretRules.EnsureDevUserPassword fails the
        // AppHost fast, before any resource starts, and this test fails with that message.
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Decisya_AppHost>(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await app.StartAsync(cancellationToken);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        // NFR-16: healthy within 60s of StartAsync returning, excluding a first-ever image
        // pull (documented in GETTING-STARTED; this test assumes the images are already
        // pulled, as they are after the first `dotnet run`).
        using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var healthLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, healthTimeout.Token);
        await notifications.WaitForResourceHealthyAsync("postgres", healthLinked.Token);
        await notifications.WaitForResourceHealthyAsync("keycloak", healthLinked.Token);
        stopwatch.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        AssertImageAnnotationMatches(model, "postgres", ContainerImages.PostgresRegistry, ContainerImages.PostgresImage,
            ContainerImages.PostgresTag, ContainerImages.PostgresSha256);
        AssertImageAnnotationMatches(model, "keycloak", ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
            ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256);

        var keycloakResource = model.Resources.OfType<IResourceWithEnvironment>().Single(r => r.Name == "keycloak");
#pragma warning disable CS0618 // see AppHostResourceTests: advisory-deprecated in Aspire.Hosting.Testing 13.5.4, reported at G6.
        var environment = await keycloakResource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Run);
#pragma warning restore CS0618

        // L-1 (#15): keys only for secret-carrying entries, never the whole dictionary.
        var keys = environment.Keys;
        foreach (var secretKey in SecretEnvironmentKeys)
        {
            keys.Should().Contain(secretKey);
        }

        environment["KC_DB"].Should().Be("postgres");
        environment["KC_DB_USERNAME"].Should().Be("keycloak");
        environment["KC_DB_URL"].Should().EndWith("/keycloak");
        _ = NonSecretEnvironmentKeys;

        using var httpClient = app.CreateHttpClient("keycloak", "http");

        using var discoveryResponse = await httpClient.GetAsync(
            "/realms/decisya/.well-known/openid-configuration", cancellationToken);
        discoveryResponse.IsSuccessStatusCode.Should().BeTrue();
        var discoveryBody = await discoveryResponse.Content.ReadAsStringAsync(cancellationToken);
        discoveryBody.Should().Contain("\"issuer\"");
        discoveryBody.Should().MatchRegex("\"issuer\"\\s*:\\s*\"[^\"]*/realms/decisya\"");

        using var loginPageResponse = await httpClient.GetAsync(
            "/realms/decisya/protocol/openid-connect/auth" +
            "?client_id=decisya-bff&response_type=code&scope=openid" +
            "&redirect_uri=https%3A%2F%2Flocalhost%3A7200%2Fsignin-oidc" +
            "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeKdcaUlVRNHuA0Q9BSM&code_challenge_method=S256&state=dev",
            cancellationToken);
        loginPageResponse.IsSuccessStatusCode.Should().BeTrue();
        var loginPageBody = await loginPageResponse.Content.ReadAsStringAsync(cancellationToken);
        loginPageBody.Should().Contain("kc-form-login");

        await app.StopAsync(cancellationToken);
    }

    private static void AssertImageAnnotationMatches(
        DistributedApplicationModel model, string resourceName, string registry, string image, string tag, string sha256)
    {
        var resource = model.Resources.Single(r => r.Name == resourceName);
        var annotation = resource.Annotations.OfType<ContainerImageAnnotation>().Single();

        annotation.Registry.Should().Be(registry);
        annotation.Image.Should().Be(image);
        annotation.Tag.Should().Be(tag);
        annotation.SHA256.Should().Be(sha256);
    }
}
