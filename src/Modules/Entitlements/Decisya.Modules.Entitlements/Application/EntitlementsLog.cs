using Decisya.Modules.Entitlements.Contracts;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>
/// The compiled log messages the admin handlers write (CA1848). Deliberately no reason
/// parameter and no command or entity parameter (G3 G4-23-05).
/// </summary>
internal static partial class EntitlementsLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Entitlements: trial started for target_tenant_id {TargetTenantId}.")]
    internal static partial void TrialStarted(ILogger logger, TenantId targetTenantId);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Entitlements: override granted for target_tenant_id {TargetTenantId}, feature {Feature}, expires_at {ExpiresAt}.")]
    internal static partial void OverrideGranted(ILogger logger, TenantId targetTenantId, FeatureKey feature, Instant? expiresAt);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Entitlements: override revoked for target_tenant_id {TargetTenantId}, feature {Feature}.")]
    internal static partial void OverrideRevoked(ILogger logger, TenantId targetTenantId, FeatureKey feature);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "Entitlements: admin command {Command} refused, ambient tenant resolution {ResolutionKind}.")]
    internal static partial void CommandForbidden(ILogger logger, string command, string resolutionKind);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "Entitlements: admin command {Command} refused, the caller has no validated user id.")]
    internal static partial void CommandActorUnknown(ILogger logger, string command);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "Entitlements: admin command {Command} refused, target_tenant_id {TargetTenantId} does not exist.")]
    internal static partial void TargetTenantNotFound(ILogger logger, string command, TenantId targetTenantId);
}
