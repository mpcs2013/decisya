using Decisya.Api.Authentication;
using Decisya.Api.Messaging;
using Decisya.Api.Tests.Authentication;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// A started Decisya host with the registrations <c>Program</c> makes for messaging
/// (<c>AddServiceDefaults</c>, <c>AddDecisyaApiAuthentication</c>, <c>AddDecisyaMessaging</c>, scope
/// validation in every environment) over the in-memory <c>TestServer</c>, from in-memory
/// configuration only. <c>Program.cs</c> is not used: it is the identity lane's file and builds
/// against placeholder connection strings.
/// </summary>
internal sealed class MessagingHost : IAsyncDisposable
{
    private MessagingHost(WebApplication app, CapturingLoggerProvider logs, ProbeRecorder recorder)
    {
        App = app;
        Logs = logs;
        Recorder = recorder;
    }

    internal WebApplication App { get; }

    internal CapturingLoggerProvider Logs { get; }

    internal ProbeRecorder Recorder { get; }

    internal IServiceProvider Services => App.Services;

    internal Wolverine.Runtime.IWolverineRuntime Runtime => App.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>();

    internal static Dictionary<string, string?> Configuration() => new(ApiStartHarness.ValidConfiguration(), StringComparer.Ordinal)
    {
        [DecisyaObservabilityOptions.UserIdHashKeyPath] = Canaries.HashKey(),
        ["AllowedHosts"] = "localhost",
    };

    internal static async Task<MessagingHost> StartAsync(
        string environmentName = "Development",
        IReadOnlyDictionary<string, string?>? extraConfiguration = null,
        Action<WebApplicationBuilder>? configure = null,
        bool probeHandlers = true,
        IReadOnlyList<Type>? handlerTypes = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });

        builder.Configuration.Sources.Clear();
        var values = Configuration();
        if (extraConfiguration is not null)
        {
            foreach (var pair in extraConfiguration)
            {
                values[pair.Key] = pair.Value;
            }
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.UseTestServer();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.AddServiceDefaults();
        builder.AddDecisyaApiAuthentication();
        var listed = new List<Type>(handlerTypes ?? []);
        if (probeHandlers)
        {
            listed.AddRange(ProbeHandlers.Types);
        }

        if (listed.Count == 0)
        {
            builder.AddDecisyaMessaging();
        }
        else
        {
            builder.AddDecisyaMessaging(listed);
        }

        var recorder = new ProbeRecorder();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddScoped<ProbeDb>();
        if (probeHandlers)
        {
            // The one Wolverine override of the test host (G3 G4-231-07): the Api is Static, the tests
            // compile the listed handlers into real chains with the test-only runtime compiler.
            builder.Services.AddWolverineRuntimeCompilation();
            builder.Services.ConfigureWolverine(
                options => options.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic);
        }

        var logs = new CapturingLoggerProvider();
        builder.Logging.AddProvider(logs);

        configure?.Invoke(builder);

        var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return new MessagingHost(app, logs, recorder);
    }

    /// <summary>
    /// A request-like scope whose caller context is already populated, the way
    /// <c>CallerContextMiddleware</c> leaves it for a tenant user.
    /// </summary>
    internal AsyncServiceScope CallerScope(TenantId tenant, string userId)
    {
        var scope = App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestCaller>()
            .Set(TenantResolution.For(tenant), userId, isPlatformAdmin: false);
        return scope;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
    }
}
