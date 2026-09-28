using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Decisya.Bff.Tests;

/// <summary>One request the double received, headers flattened so it can be inspected after
/// the request has completed.</summary>
internal sealed record RecordedApiRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string[]> Headers);

/// <summary>
/// #19 G2: the test double standing in for <c>Decisya.Api</c> — a minimal
/// <see cref="WebApplication"/> on <c>http://127.0.0.1:0</c>. One catch-all endpoint records
/// every request into <see cref="Requests"/> and answers <c>200 {"ok":true}</c>. It never
/// echoes a header or body on its normal path; the two test-only failure modes
/// (<see cref="RespondWithSetCookie"/>, <see cref="RespondWith5xxLeakingAuthorization"/>) exist
/// only to simulate what a hostile or misbehaving upstream could do (G4-19-02).
/// </summary>
internal sealed class ApiDouble : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiDouble(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>The double's base address, e.g. <c>http://127.0.0.1:54321</c> — the value
    /// tests hand to <c>BffFactoryFactory.Create(..., apiAddress: ...)</c> as <c>Bff:Api:Address</c>.</summary>
    internal string Address { get; }

    internal ConcurrentQueue<RecordedApiRequest> Requests { get; } = new();

    /// <summary>Every response carries an extra <c>Set-Cookie</c> pair, simulating an upstream
    /// that (accidentally or otherwise) tries to set a cookie on the BFF's own origin (T-04).</summary>
    internal bool RespondWithSetCookie { get; set; }

    /// <summary>Every response is a 500 whose body echoes the received <c>Authorization</c>
    /// header value, simulating a developer-exception-page-style leak (T-05). Set
    /// <see cref="LeakingBodyContentType"/> to choose the body's declared content type.</summary>
    internal bool RespondWith5xxLeakingAuthorization { get; set; }

    internal string LeakingBodyContentType { get; set; } = "text/plain";

    internal static async Task<ApiDouble> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        var doubleBox = new ApiDouble[1];

        app.MapMethods("/{**catch-all}", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"], async (HttpContext context) =>
        {
            var current = doubleBox[0];
            var headers = context.Request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.Where(value => value is not null).Select(value => value!).ToArray(),
                StringComparer.OrdinalIgnoreCase);
            current.Requests.Enqueue(new RecordedApiRequest(
                context.Request.Method, context.Request.Path.Value ?? string.Empty, context.Request.QueryString.Value ?? string.Empty, headers));

            if (current.RespondWithSetCookie)
            {
                context.Response.Headers.Append("Set-Cookie", "__Host-decisya-session=hijacked; Path=/");
                context.Response.Headers.Append("Set-Cookie", "evil-cookie=1; Path=/");
            }

            if (current.RespondWith5xxLeakingAuthorization)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = current.LeakingBodyContentType;
                var authorizationHeader = context.Request.Headers.Authorization.ToString();
                await context.Response.WriteAsync($"Authorization: {authorizationHeader}", cancellationToken).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("{\"ok\":true}", cancellationToken).ConfigureAwait(false);
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addressesFeature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The ApiDouble server exposes no IServerAddressesFeature.");
        var address = addressesFeature.Addresses.First();

        var apiDouble = new ApiDouble(app, address);
        doubleBox[0] = apiDouble;

        return apiDouble;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
