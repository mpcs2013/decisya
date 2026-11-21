using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Ledger.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// Renames an account (issue #341, G1 Story 5, G2 "Write flows"): one explicit transaction, load by id through
/// the tenant filter (another space's id is as invisible as a missing one), rename, save, append the audit
/// record, commit. Two simultaneous renames: the last writer wins and each gets one audit record. A
/// <see cref="DbUpdateConcurrencyException"/> is not caught: it reaches the API's generic 404 handler (NFR-33).
/// </summary>
internal sealed class RenameAccountHandler(
    LedgerDbContext db,
    ICurrentTenant tenant,
    IAuditWriter audit,
    IAccountActivity activity,
    ILogger<RenameAccountHandler> logger)
{
    public async Task<AccountCommandResult> HandleAsync(RenameAccount command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (tenant.Resolution.Kind != TenantResolutionKind.Tenant)
        {
            LedgerWriteReport.Refused(logger, null, LedgerTelemetry.OperationRename, LedgerTelemetry.ReasonForbidden);
            return new AccountCommandResult(LedgerOutcome.Forbidden, null);
        }

        using var span = LedgerTelemetry.StartSpan("Ledger.RenameAccount", tenant);

        AccountCommandResult result;
        try
        {
            result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LedgerWriteReport.Failed(span, LedgerTelemetry.OperationRename);
            throw;
        }

        LedgerWriteReport.Completed(logger, span, LedgerTelemetry.OperationRename, result);
        return result;
    }

    private async Task<AccountCommandResult> ExecuteAsync(RenameAccount command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Resolution.TenantId;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var account = await db.Accounts
            .SingleOrDefaultAsync(a => a.Id == command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return new AccountCommandResult(LedgerOutcome.NotFound, null);
        }

        account.Rename(command.Name);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await audit.AppendAsync(
            new AuditEntry(tenantId, AuditAction.LedgerAccountRename, null, account.Id),
            tx.GetDbTransaction(),
            cancellationToken).ConfigureAwait(false);

        var summaries = await activity.SummarizeAsync([account.Id], cancellationToken).ConfigureAwait(false);
        var dto = AccountMapper.ToDto(account, summaries.GetValueOrDefault(account.Id));

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AccountCommandResult(LedgerOutcome.Succeeded, dto);
    }
}
