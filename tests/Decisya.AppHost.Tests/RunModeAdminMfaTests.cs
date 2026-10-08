using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Issue #121 (G2 D3, G3 G4-121-01 d): the dev AppHost turns the Api's admin-MFA requirement off
/// in run mode only, because the dev realm has no OTP and <c>dev-admin</c> gets <c>acr</c> "1".
/// The publish half of this pair (the generated Compose file never carries the key) is
/// <see cref="ComposeStackPublishTests"/>.
/// </summary>
/// <remarks>
/// The model is built, never started: no DCP, no container, no Docker. The environment callbacks
/// are run into a dictionary and only the one key is read, so no parameter is resolved and no
/// secret is generated or persisted. The dev-user password parameter only has to pass
/// <c>RealmSecretRules</c>; the value below is a test-only dummy that is never used.
/// </remarks>
[Trait("Category", "AppHost")]
public class RunModeAdminMfaTests
{
    internal const string EnvironmentKey = "Authentication__RequireAdminMfa";

    private const string DummyDevUserPassword = "RunModeModelTestOnlyDummy01";

    [Fact]
    public async Task Run_mode_turns_the_admin_MFA_requirement_off_on_decisya_api_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var volumeName = TestAppHostIsolation.CreateVolumeName();
        string[] args = [.. TestAppHostIsolation.AsCommandLineArgs(volumeName), $"--Parameters:dev-user-password={DummyDevUserPassword}"];

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Decisya_AppHost>(args, cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var api = model.Resources.Single(r => r.Name == "decisya-api");
        var apiEnvironment = await CallbackEnvironmentAsync(appHost, api, cancellationToken);

        apiEnvironment.Should().ContainKey(EnvironmentKey);
        apiEnvironment[EnvironmentKey].Should().Be("false");

        foreach (var other in model.Resources.Where(r => r.Name != "decisya-api" && r.Annotations.OfType<EnvironmentCallbackAnnotation>().Any()))
        {
            var environment = await CallbackEnvironmentAsync(appHost, other, cancellationToken);
            environment.Keys.Should().NotContain(EnvironmentKey, $"only decisya-api consumes it; {other.Name} must not carry it");
        }
    }

    private static async Task<Dictionary<string, object>> CallbackEnvironmentAsync(
        IDistributedApplicationTestingBuilder appHost, IResource resource, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(appHost.ExecutionContext, resource, environment, cancellationToken);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return environment;
    }
}
