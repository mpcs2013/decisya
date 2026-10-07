using System.Net;
using System.Net.Sockets;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Production;

/// <summary>
/// A real <c>WebApplication</c> with <c>AddServiceDefaults</c>, listening on two loopback
/// ports: an ephemeral application port and a management port the test picked (the
/// <c>Decisya:Management:Port</c> test seam). It serves <c>/ping</c> on the application
/// pipeline, which echoes the remote address and scheme the pipeline saw.
/// </summary>
internal sealed class ProductionTestHost : IAsyncDisposable
{
    internal const string AllowedHost = "app.test";

    private ProductionTestHost(WebApplication app, Uri appUri, Uri managementUri)
    {
        App = app;
        AppUri = appUri;
        ManagementUri = managementUri;
    }

    internal WebApplication App { get; }

    internal Uri AppUri { get; }

    internal Uri ManagementUri { get; }

    /// <summary>
    /// Starts the host. <paramref name="overrides"/> win over the defaults (a <see langword="null"/>
    /// value removes a default, for example <c>AllowedHosts</c>).
    /// </summary>
    internal static async Task<ProductionTestHost> StartAsync(
        string environment,
        IDictionary<string, string?>? overrides = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? beforeEndpoints = null,
        CancellationToken cancellationToken = default)
    {
        var managementPort = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });

        var settings = new Dictionary<string, string?>
        {
            [DecisyaObservabilityOptions.UserIdHashKeyPath] = Canaries.HashKey(),
            ["AllowedHosts"] = AllowedHost,
            ["Decisya:Management:Port"] = managementPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseUrls(
            "http://127.0.0.1:0",
            $"http://127.0.0.1:{managementPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        builder.AddServiceDefaults();
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        beforeEndpoints?.Invoke(app);
        app.MapDefaultEndpoints();
        app.MapGet("/ping", (HttpContext context) =>
            $"{context.Connection.RemoteIpAddress}|{context.Request.Scheme}");

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        var uris = app.Urls.Select(static url => new Uri(url)).ToArray();
        var management = uris.Single(uri => uri.Port == managementPort);
        var application = uris.Single(uri => uri.Port != managementPort);
        return new ProductionTestHost(app, application, management);
    }

    internal static HttpClient NewClient() =>
        new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });

    internal static async Task<HttpResponseMessage> GetAsync(
        Uri baseUri, string path, string? host, CancellationToken cancellationToken)
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return await client.SendAsync(request, cancellationToken);
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
