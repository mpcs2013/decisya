using Decisya.Api.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// The direct-start pattern (#164 G2 D4, O-004): builds <em>Decisya's own</em> registration
/// (<c>AddDecisyaApiAuthentication</c>) on a <see cref="WebApplication"/> over the in-memory
/// <c>TestServer</c>, from in-memory configuration only, and starts it on the test thread. There is no
/// <c>WebApplicationFactory&lt;Program&gt;</c> and no <c>Record.Exception(() =&gt; factory.Server)</c>:
/// a host built from the top-level-statements entry point runs through <c>HostFactoryResolver</c>'s
/// diagnostic-listener interception, which is not safe against a concurrently failing host.
/// </summary>
internal static class ApiStartHarness
{
    internal const string AuthorityKey = "Api:Jwt:Authority";
    internal const string AudienceKey = "Api:Jwt:Audience";
    internal const string EventNameKey = "Api:Jwt:Events:TokenRejected:Name";
    internal const string EventIdKey = "Api:Jwt:Events:TokenRejected:Id";
    internal const string TrustedRootPathKey = "Api:Jwt:TrustedRootPath";

    /// <summary>A complete, valid configuration for a Development host (the authority is never contacted).</summary>
    internal static Dictionary<string, string?> ValidConfiguration() => new(StringComparer.Ordinal)
    {
        [AuthorityKey] = DecisyaTestTokens.Issuer,
        [AudienceKey] = DecisyaTestTokens.Audience,
        [EventNameKey] = "auth.token.rejected",
        [EventIdKey] = "2003",
    };

    /// <summary>
    /// Builds and starts the host. Returns every failure message (empty when it started); a started host
    /// is stopped and disposed again.
    /// </summary>
    internal static async Task<List<string>> StartAndCollectFailuresAsync(
        IReadOnlyDictionary<string, string?> configuration,
        string environmentName = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });

        // In-memory configuration only: no appsettings.json from the content root, no environment variable.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.WebHost.UseTestServer();
        builder.AddDecisyaApiAuthentication();

        WebApplication? app = null;
        try
        {
            app = builder.Build();
            await app.StartAsync();
            return [];
        }
        catch (Exception exception)
        {
            return Flatten(exception);
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    await app.StopAsync();
                }
                catch (Exception)
                {
                    // The host never started: nothing to stop.
                }

                await app.DisposeAsync();
            }
        }
    }

    internal static List<string> Flatten(Exception? exception) => exception switch
    {
        null => [],
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten).ToList(),
        OptionsValidationException options => options.Failures.ToList(),
        _ => [exception.Message, .. Flatten(exception.InnerException)],
    };
}
