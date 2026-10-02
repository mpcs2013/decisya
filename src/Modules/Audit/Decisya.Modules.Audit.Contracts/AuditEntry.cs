using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Audit.Contracts;

/// <summary>
/// What the audited module states about one succeeded command (issue #24, G2; ADR-0013).
/// Deliberately minimal: the actor, the time and the trace id are not part of the entry.
/// The Audit module reads them itself (the ambient <c>ICurrentCaller</c>, <c>IClock</c> and
/// <c>Activity.Current</c>), so a caller can neither forge nor omit them. No free text, reason,
/// command payload or personal data can be expressed here (G1 Story 3).
/// </summary>
/// <param name="TenantId">The command's target tenant. The record is <c>ITenantScoped</c> to it (G1 Q1).</param>
/// <param name="Action">One value of the closed <see cref="AuditAction"/> set.</param>
/// <param name="FeatureKey">
/// The catalog feature key (shape <c>module.feature</c>, at most 64 characters) for the override
/// actions; <see langword="null"/> for <see cref="AuditAction.EntitlementsTrialStart"/>. Any
/// other shape, or a mismatch with <paramref name="Action"/>, is rejected before the database.
/// </param>
public sealed record AuditEntry(TenantId TenantId, AuditAction Action, string? FeatureKey);
