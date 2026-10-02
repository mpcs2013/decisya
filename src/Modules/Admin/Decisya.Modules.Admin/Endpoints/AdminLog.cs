using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Admin.Endpoints;

/// <summary>
/// The compiled log messages the admin endpoints write (CA1848). Deliberately no reason, body,
/// claim, raw route value or exception parameter (G3 G4-25-04).
/// </summary>
internal static partial class AdminLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "Admin: access refused for action {Action}, reason {Reason}, target_tenant_id {TargetTenantId}.")]
    internal static partial void AccessRefused(ILogger logger, string action, string reason, string? targetTenantId);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "Admin: request for action {Action} rejected, reason_code {ReasonCode}.")]
    internal static partial void RequestRejected(ILogger logger, string action, string reasonCode);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Admin: action {Action} completed with outcome {Outcome}, target_tenant_id {TargetTenantId}.")]
    internal static partial void RequestCompleted(ILogger logger, string action, string outcome, TenantId targetTenantId);
}
