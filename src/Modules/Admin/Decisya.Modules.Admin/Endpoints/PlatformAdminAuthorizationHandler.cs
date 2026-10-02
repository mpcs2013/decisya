using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Admin.Endpoints;

/// <summary>
/// Succeeds only when <see cref="ICurrentCaller.IsPlatformAdmin"/> <b>and</b> the resolution is
/// <see cref="TenantResolutionKind.None"/> (issue #25, G2 D2; G3 G4-25-01). It reads no claim: the
/// API's one claim parser has already set the fact, once per request. Registered scoped, so it
/// always sees the request's own caller. Both checks are repeated by each Entitlements handler.
/// </summary>
internal sealed class PlatformAdminAuthorizationHandler(
    ICurrentCaller caller,
    ICurrentTenant currentTenant,
    ILogger<PlatformAdminAuthorizationHandler> logger) : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        var kind = currentTenant.Resolution.Kind;

        if (kind == TenantResolutionKind.None && caller.IsPlatformAdmin)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var (action, target) = Describe(context.Resource);
            var reason = kind == TenantResolutionKind.None ? "not_platform_admin" : "tenant_scoped";
            AdminLog.AccessRefused(logger, action, reason, target);
            AdminTelemetry.RecordRequest(action, AdminTelemetry.OutcomeForbidden);
        }

        return Task.CompletedTask;
    }

    private static (string Action, string? TargetTenantId) Describe(object? resource)
    {
        var httpContext = resource as HttpContext;
        var endpoint = httpContext?.GetEndpoint() ?? resource as Endpoint;
        var action = endpoint?.Metadata.GetMetadata<AdminActionMetadata>()?.Action ?? AdminActionMetadata.Unknown;

        // The raw route value is never logged: only an id that parses as a TenantId.
        string? target = null;
        if (httpContext?.Request.RouteValues["tenantId"] is string text && TenantId.TryParse(text, out var tenantId))
        {
            target = tenantId.ToString();
        }

        return (action, target);
    }
}
