using Decisya.Modules.Tenancy.Application;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Tenancy.Endpoints;

/// <summary>
/// Runs JIT provisioning for every authorized endpoint (issue #21, G2, D2; G4-21-02, G4-21-03).
/// Exposed as <c>TenancyModule.UseTenancyMembership</c>. Must run after <c>UseRouting</c> (so
/// <c>HttpContext.GetEndpoint()</c> resolves) and before <c>UseAuthorization</c> (so the
/// <c>Tenancy.Owner</c> policy never runs before the membership exists).
/// </summary>
internal static class TenantMembershipMiddleware
{
    public static IApplicationBuilder Use(IApplicationBuilder app) =>
        app.Use(InvokeAsync);

    private static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var currentTenant = context.RequestServices.GetRequiredService<ICurrentTenant>();
        var endpoint = context.GetEndpoint();

        var gated = currentTenant.Resolution.Kind == TenantResolutionKind.Tenant &&
            endpoint is not null &&
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null &&
            endpoint.Metadata.GetMetadata<SkipTenantMembershipMetadata>() is null;

        if (!gated)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var gate = context.RequestServices.GetRequiredService<TenantMembershipGate>();
        var result = await gate.EnsureAsync(context.RequestAborted).ConfigureAwait(false);

        if (result == TenantMembershipResult.Refused)
        {
            await WriteForbiddenAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The same generic <c>ProblemDetails</c> shape every #21 403 uses (G2): <c>status</c>,
    /// <c>title</c> and <c>traceId</c> only, with <c>Cache-Control: no-store</c>. Never a
    /// reason code or the claim value in the body; the caller cannot tell a refused JIT
    /// provisioning apart from any other 403.
    /// </summary>
    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        var problemDetailsService = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Http.IProblemDetailsService>();
        await problemDetailsService.WriteAsync(new Microsoft.AspNetCore.Http.ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = StatusCodes.Status403Forbidden },
        }).ConfigureAwait(false);
    }
}
