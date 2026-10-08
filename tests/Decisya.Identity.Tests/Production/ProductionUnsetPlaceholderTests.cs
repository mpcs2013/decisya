using Decisya.AppHost;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, Story 2 (first scenario) and G3 G4-121-03 (d): the layer under the wrapper. With the
/// wrapper bypassed and the realm's required values left unset at the Keycloak level, Keycloak must not
/// serve a realm that carries a literal <c>${...}</c> in a redirect URI, secret or origin; it refuses to
/// start (fail closed). Keycloak names the client, not the variable: naming the variable is the job of
/// the wrapper (<see cref="ProductionWrapperTests"/>) and of Compose's required-variable check.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductionUnsetPlaceholderTests
{
    [Fact]
    public async Task A_realm_value_left_unset_makes_keycloak_refuse_to_serve_the_realm_with_a_literal_placeholder()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = ContainerImages.Reference(
            ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
            ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256);

        // The wrapper is bypassed on purpose: no DECISYA_* value, no secret file. The password list is
        // mounted so the placeholders are the only thing that can stop the import.
        var container = new ContainerBuilder(image)
            .WithPortBinding(8080, true)
            .WithPortBinding(9000, true)
            .WithEntrypoint("/opt/keycloak/bin/kc.sh")
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", ProductionKeycloak.Hex(24))
            .WithEnvironment("KC_HEALTH_ENABLED", "true")
            .WithResourceMapping(
                ProductionKeycloak.ReadRepoFile("deploy/keycloak/production/realm-decisya.json"),
                "/opt/keycloak/data/import/realm-decisya.json")
            .WithResourceMapping(
                ProductionKeycloak.ReadRepoFile("deploy/keycloak/production/common-passwords.txt"),
                "/opt/keycloak/data/password-blacklists/common-passwords.txt")
            // No HTTP wait strategy: the expected outcome is a Keycloak that never becomes ready, and a
            // fixed bound on readiness turned slow augmentation into a false failure. The test instead
            // waits for the outcome itself (below).
            .Build();
        try
        {
            // The container runs `start-dev` on purpose: it is the one launch mode that needs neither a
            // hostname nor TLS, and the import (the thing under test) runs identically in every mode. The
            // production launch path, with the wrapper, is covered by ProductionWrapperTests and the stack tests.
            //
            // Condition-based wait: the import is refused (the log names the invalid client), Keycloak
            // reports it is listening (the import was not refused), or the container exits. The bound is
            // only a safety net: the lane runs many Keycloak containers at once and the Quarkus
            // augmentation alone took 205 s under that load, so a short bound measured the load, not Keycloak.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));

            try
            {
                await container.StartAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The container may already have exited while starting; the log decides below.
                ProductionKeycloak.Record($"unset realm values, wrapper bypassed: start threw {exception.GetType().Name}");
            }

            var started = false;
            var log = string.Empty;
            while (!timeout.IsCancellationRequested)
            {
                var (stdout, stderr) = await container.GetLogsAsync(ct: CancellationToken.None);
                log = stdout + stderr;
                if (log.Contains("Invalid client decisya-bff", StringComparison.Ordinal))
                {
                    break;
                }

                if (log.Contains("Listening on:", StringComparison.Ordinal))
                {
                    started = true;
                    break;
                }

                if (container.State != TestcontainersStates.Running)
                {
                    // Exited: read the final log once more (it may have been flushed after the last read).
                    (stdout, stderr) = await container.GetLogsAsync(ct: CancellationToken.None);
                    log = stdout + stderr;
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            ProductionKeycloak.Record($"unset realm values, wrapper bypassed: container started={started}; log names a DECISYA_ value={log.Contains("DECISYA_", StringComparison.Ordinal)}");

            if (started)
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}") };
                using var response = await http.GetAsync($"/realms/{ProductionKeycloak.Realm}/.well-known/openid-configuration", ct);
                response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound, "no realm may exist with an unresolved placeholder");
            }
            else
            {
                // Keycloak names the client, not the variable; naming the variable is the wrapper's and Compose's job.
                log.Should().Contain("Invalid client decisya-bff", "an unresolved placeholder makes the client's URLs invalid, so the import is refused");
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
