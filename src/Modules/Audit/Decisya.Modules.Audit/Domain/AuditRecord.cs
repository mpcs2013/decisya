using Decisya.Modules.Audit.Contracts;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Audit.Domain;

/// <summary>
/// One append-only audit record of a succeeded cross-tenant admin command (issue #24, G2).
/// <see cref="TenantId"/> is the command's target tenant and the actor has its own column
/// (G1 Q1). Immutable by construction: every property is get-only, there is no mutator and no
/// navigation, and the database role can only insert (ADR-0013).
/// </summary>
internal sealed class AuditRecord : ITenantScoped
{
    private AuditRecord()
    {
        // EF Core materialization constructor.
    }

    private AuditRecord(
        TenantId tenantId, AuditAction action, string? featureKey, string actorUserId, Instant occurredAt, string? traceId, Guid? subjectId)
    {
        Id = Guid.CreateVersion7();
        SubjectId = subjectId;
        TenantId = tenantId;
        OccurredAt = occurredAt;
        ActorUserId = actorUserId;
        Action = action;
        Outcome = AuditOutcome.Succeeded;
        FeatureKey = featureKey;
        TraceId = traceId;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Instant OccurredAt { get; }

    /// <summary>The opaque IdP <c>sub</c> of the admin who ran the command. Pseudonymous personal data (G1 R-2): masked if ever rendered.</summary>
    [Sensitive]
    public string ActorUserId { get; } = string.Empty;

    public AuditAction Action { get; }

    public AuditOutcome Outcome { get; }

    public string? FeatureKey { get; }

    public string? TraceId { get; }

    /// <summary>The opaque id of the changed object (a ledger account's id); never a name, amount or other value (ADR-0013 amendment 1). Null for the Entitlements actions.</summary>
    public Guid? SubjectId { get; }

    public static AuditRecord Create(
        TenantId tenantId, AuditAction action, string? featureKey, string actorUserId, Instant occurredAt, string? traceId, Guid? subjectId = null) =>
        new(tenantId, action, featureKey, actorUserId, occurredAt, traceId, subjectId);
}
