using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Ledger.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// Changes an account's currency and restates its opening balance (issue #341, G1 Story 5, G2 "Write flows"):
/// one explicit transaction, load by id through the tenant filter, refuse an unchanged currency, ask
/// <see cref="IAccountActivity"/> whether the account holds a transaction (A3; the currency is then locked),
/// change, save, append the audit record, commit. A refusal disposes the transaction uncommitted.
/// </summary>
internal sealed class ChangeAccountCurrencyHandler(
    LedgerDbContext db,
    ICurrentTenant tenant,
    IAuditWriter audit,
    IAccountActivity activity,
    ILogger<ChangeAccountCurrencyHandler> logger)
{
    public async Task<AccountCommandResult> HandleAsync(ChangeAccountCurrency command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (tenant.Resolution.Kind != TenantResolutionKind.Tenant)
        {
            LedgerWriteReport.Refused(logger, null, LedgerTelemetry.OperationCurrencyChange, LedgerTelemetry.ReasonForbidden);
            return new AccountCommandResult(LedgerOutcome.Forbidden, null);
        }

        using var span = LedgerTelemetry.StartSpan("Ledger.ChangeAccountCurrency", tenant);

        AccountCommandResult result;
        try
        {
            result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LedgerWriteReport.Failed(span, LedgerTelemetry.OperationCurrencyChange);
            throw;
        }

        LedgerWriteReport.Completed(logger, span, LedgerTelemetry.OperationCurrencyChange, result);
        return result;
    }

    private async Task<AccountCommandResult> ExecuteAsync(ChangeAccountCurrency command, CancellationToken cancellationToken)
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

        if (account.Currency == command.Currency)
        {
            return new AccountCommandResult(LedgerOutcome.CurrencyUnchanged, null);
        }

        var summaries = await activity.SummarizeAsync([account.Id], cancellationToken).ConfigureAwait(false);
        var holdsTransaction = summaries.GetValueOrDefault(account.Id).HoldsTransaction;

        if (!account.TryChangeCurrency(command.Currency, command.OpeningBalance, command.OpeningDate, holdsTransaction))
        {
            return new AccountCommandResult(LedgerOutcome.CurrencyLocked, null);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await audit.AppendAsync(
            new AuditEntry(tenantId, AuditAction.LedgerAccountCurrencyChange, null, account.Id),
            tx.GetDbTransaction(),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The change only happens while the account holds no transaction, so its net is zero.
        return new AccountCommandResult(LedgerOutcome.Succeeded, AccountMapper.ToDto(account, default), command.AccountId);
    }
}
