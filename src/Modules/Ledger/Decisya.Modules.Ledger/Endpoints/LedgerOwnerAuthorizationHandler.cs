using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Tenancy.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Ledger.Endpoints;

/// <summary>
/// Succeeds only when the caller's own role in the ambient space is exactly <see cref="TenantMemberRole.Owner"/>
/// (issue #341, G2 D3; G3 M341-01 (b)). Deny by default: no membership, no tenant, a member, or any role a
/// later release adds, all fail. It runs before any ledger query and before the 404 check, so a member learns
/// nothing about ids. A refusal leaves one Warning and one count (A5), with no user or tenant value beyond the
/// enrichment scope. A database failure propagates as the generic 500; it is never "allowed". Registered
/// scoped, so it sees the request's own <see cref="ICallerTenantRole"/>.
/// </summary>
internal sealed class LedgerOwnerAuthorizationHandler(
    ICallerTenantRole roles,
    ILogger<LedgerOwnerAuthorizationHandler> logger) : AuthorizationHandler<LedgerOwnerRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, LedgerOwnerRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cancellationToken = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;

        var role = await roles.GetAsync(cancellationToken).ConfigureAwait(false);
        if (role == TenantMemberRole.Owner)
        {
            context.Succeed(requirement);
            return;
        }

        var operation = OperationOf(context.Resource);
        LedgerLog.WriteForbidden(logger, operation);
        LedgerTelemetry.Record(operation, LedgerTelemetry.ResultRefused, LedgerTelemetry.ReasonForbidden);
    }

    private static string OperationOf(object? resource)
    {
        var endpoint = resource switch
        {
            HttpContext http => http.GetEndpoint(),
            Endpoint e => e,
            _ => null,
        };

        return endpoint?.Metadata.GetMetadata<LedgerOperationMetadata>()?.Operation ?? "unknown";
    }
}
