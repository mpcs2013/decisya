using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.Api.Authentication;

/// <summary>
/// Resolves the current request's caller and tenant from the validated principal's own claims,
/// before routing (issue #21, G2; G3 G4-21-02, closes #22 B-2; #20 B-5). Exposed as
/// <c>UseCallerContext</c>. Must run after <c>UseAuthentication</c> and before
/// <c>UseRouting</c>, so a malformed <c>tenant_id</c> claim — or claims that cannot be
/// trusted as a single identity — never reach endpoint selection, an authorization handler or
/// a <c>TenantDbContext</c> query (Story 4).
/// </summary>
internal static class CallerContextMiddleware
{
    private const string LoggerCategory = "Decisya.Api.Authentication.CallerContextMiddleware";

    public static IApplicationBuilder Use(IApplicationBuilder app) => app.Use(InvokeAsync);

    private static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Anonymous: /health, /alive, or a caller the fallback authorization policy will
            // reject with 401 further down the pipeline. Nothing here sets a resolution or an
            // enrichment scope for a caller nobody has validated yet.
            await next(context).ConfigureAwait(false);
            return;
        }

        var identity = CallerIdentity.From(context.User);
        if (identity is null)
        {
            CallerContextMiddlewareLog.InvalidIdentity(ResolveLogger(context));
            await WriteForbiddenAsync(context).ConfigureAwait(false);
            return;
        }

        var resolution = TenantResolution.FromClaim(identity.TenantId);
        if (resolution.Kind == TenantResolutionKind.Invalid)
        {
            CallerContextMiddlewareLog.InvalidTenantClaim(ResolveLogger(context));
            await WriteForbiddenAsync(context).ConfigureAwait(false);
            return;
        }

        var requestCaller = context.RequestServices.GetRequiredService<RequestCaller>();
        var isPlatformAdmin = identity.HasPlatformAdminRole && resolution.Kind == TenantResolutionKind.None;
        if (identity.HasPlatformAdminRole && resolution.Kind == TenantResolutionKind.Tenant)
        {
            // R-2: the role together with a tenant_id is a Keycloak misconfiguration. The caller
            // stays a tenant user and is never an admin. Fixed text: no claim value.
            CallerContextMiddlewareLog.PlatformAdminRoleWithTenant(ResolveLogger(context));
        }

        requestCaller.Set(resolution, identity.UserId, isPlatformAdmin, identity.HasMfaLevel);

        var enrichment = context.RequestServices.GetRequiredService<ILogEnrichmentContext>();
        var tenantIdForLogs = resolution.Kind == TenantResolutionKind.Tenant
            ? resolution.TenantId.ToString()
            : null;

        using (enrichment.Begin(tenantIdForLogs, identity.UserId))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    private static ILogger ResolveLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

    /// <summary>
    /// The same generic 403 <c>ProblemDetails</c> shape every #21 403 uses (G2): <c>status</c>,
    /// <c>title</c> and <c>traceId</c> only, with <c>Cache-Control: no-store</c>. Never a
    /// reason code or the claim value in the body — the caller cannot tell an invalid identity
    /// apart from a malformed tenant claim or a refused JIT provisioning.
    /// </summary>
    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = StatusCodes.Status403Forbidden },
        }).ConfigureAwait(false);
    }
}
