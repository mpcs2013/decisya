using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Decisya.ServiceDefaults.Production;

/// <summary>
/// Serves <c>/health</c> and <c>/alive</c> for requests that arrived on the management local
/// port, and nothing else on that port (issue #120, D6 and G4-120-02).
/// </summary>
/// <remarks>
/// <para>
/// It is registered first, so its middleware is the outermost. That has three effects, all
/// wanted: host filtering and the fallback authorization policy never see a management
/// request (the probe sends <c>Host: localhost:8081</c> and <c>AllowedHosts</c> is never
/// widened for it); a management request never reaches the application pipeline; and a
/// request on the application port never reaches this branch, so <c>/health</c> there is a
/// plain 404 whatever its <c>Host</c> header says.
/// </para>
/// <para>
/// The response is the status word only (<c>Healthy</c>, <c>Degraded</c> or
/// <c>Unhealthy</c>), never a check name, description or exception.
/// </para>
/// </remarks>
internal sealed class ManagementHealthStartupFilter : IStartupFilter
{
    private const string HealthPath = "/health";
    private const string AlivePath = "/alive";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            var port = ManagementPort.Resolve(app.ApplicationServices.GetRequiredService<IConfiguration>());

            app.MapWhen(
                context => context.Connection.LocalPort == port,
                branch => branch.Run(HandleAsync));

            next(app);
        };
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Func<HealthCheckRegistration, bool>? predicate;
        if (request.Path.Equals(HealthPath, StringComparison.Ordinal))
        {
            predicate = null;
        }
        else if (request.Path.Equals(AlivePath, StringComparison.Ordinal))
        {
            predicate = static registration => registration.Tags.Contains("live");
        }
        else
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var service = context.RequestServices.GetRequiredService<HealthCheckService>();
        var report = await service.CheckHealthAsync(predicate, context.RequestAborted);

        response.StatusCode = report.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;
        response.ContentType = "text/plain";

        if (!HttpMethods.IsHead(request.Method))
        {
            await response.WriteAsync(report.Status.ToString());
        }
    }
}
