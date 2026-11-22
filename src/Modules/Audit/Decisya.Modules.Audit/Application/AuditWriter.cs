using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Audit.Domain;
using Decisya.Modules.Audit.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Decisya.Modules.Audit.Application;

/// <summary>
/// Appends one audit record inside the audited command's own transaction (issue #24, G2;
/// ADR-0013). Insert only, on the caller's connection: it never opens a connection, commits,
/// rolls back, or runs SQL of its own. Its whole use of the <see cref="DbTransaction"/> is to read
/// <c>.Connection</c> and to pass it to <c>UseTransactionAsync</c> (G3 G4-24-03; <c>RawAdoNetRule</c> and
/// <c>ExplicitTransactionRule</c>). The actor, time and trace id come from this type's own sources,
/// never from the caller, so they can be neither forged nor omitted. It logs nothing, and every
/// exception message is a fixed string that never contains entry or caller values (G3 G4-24-05).
/// </summary>
[AllowCrossTenant("Writes the audit record of a cross-tenant admin command in a context scoped to the command's target tenant, on the caller's own transaction (ADR-0012, ADR-0013). Insert only.")]
internal sealed partial class AuditWriter(ICurrentTenant ambient, ICurrentCaller caller, IClock clock) : IAuditWriter
{
    private const int MaxFeatureKeyLength = 64;
    private const int MaxActorLength = 255;

    [GeneratedRegex(@"\A[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureKeyShape();

    public async Task AppendAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(transaction);

        var connection = transaction.Connection ?? throw new InvalidOperationException(
            "The audit record was refused: the transaction has already completed.");

        ValidateEntry(entry);

        // ADR-0013 amendment 1 point 4, before any SQL. The Entitlements family keeps the old rule (None,
        // or Tenant equal to the entry's tenant). The ledger family acts for a tenant user: ambient Tenant
        // equal to the entry's tenant is required, so None (a platform admin) and Invalid are refused.
        var resolution = ambient.Resolution;
        var tenantMatches = resolution.Kind == TenantResolutionKind.Tenant && resolution.TenantId.Equals(entry.TenantId);
        var ambientAllowed = IsLedgerAction(entry.Action)
            ? tenantMatches
            : resolution.Kind == TenantResolutionKind.None || tenantMatches;
        if (!ambientAllowed)
        {
            throw new InvalidOperationException(
                "The audit record was refused: the ambient tenant does not match the entry's target tenant.");
        }

        var actor = caller.UserId;
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > MaxActorLength)
        {
            throw new InvalidOperationException("The audit record was refused: the caller has no valid user id.");
        }

        var now = clock.GetCurrentInstant();
        var traceId = Activity.Current is { } current && current.TraceId != default
            ? current.TraceId.ToHexString()
            : null;

        using var activity = AuditModule.ActivitySource.StartActivity("Audit.Append");
        activity?.SetTag("decisya.audit.action", AuditActionConverter.ToCode(entry.Action));

        await using var db = new AuditDbContext(
            AuditDbContextOptions.ForConnection(connection),
            new TargetTenant(TenantResolution.For(entry.TenantId)));

        await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

        db.Records.Add(AuditRecord.Create(entry.TenantId, entry.Action, entry.FeatureKey, actor, now, traceId, entry.SubjectId));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateEntry(AuditEntry entry)
    {
        if (!entry.TenantId.IsInitialized)
        {
            throw new ArgumentException("The audit entry's target tenant is not initialized.", nameof(entry));
        }

        if (!Enum.IsDefined(entry.Action))
        {
            throw new ArgumentException("The audit entry's action is not defined.", nameof(entry));
        }

        // Per-action shape (ADR-0013 amendment 1 point 3): overrides need a feature key and no subject;
        // trial start needs neither; the ledger actions need a non-empty subject and no feature key.
        var needsFeature = entry.Action is AuditAction.EntitlementsOverrideGrant or AuditAction.EntitlementsOverrideRevoke;
        if (needsFeature != (entry.FeatureKey is not null))
        {
            throw new ArgumentException("The audit entry's feature key does not match its action.", nameof(entry));
        }

        var needsSubject = IsLedgerAction(entry.Action);
        if (needsSubject != (entry.SubjectId is not null) || entry.SubjectId == Guid.Empty)
        {
            throw new ArgumentException("The audit entry's subject id does not match its action.", nameof(entry));
        }

        if (entry.FeatureKey is { } key && (key.Length > MaxFeatureKeyLength || !FeatureKeyShape().IsMatch(key)))
        {
            throw new ArgumentException("The audit entry's feature key is not shaped module.feature.", nameof(entry));
        }
    }

    private static bool IsLedgerAction(AuditAction action) =>
        action is AuditAction.LedgerAccountCreate or AuditAction.LedgerAccountRename or AuditAction.LedgerAccountCurrencyChange;
}
