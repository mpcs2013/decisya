using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Ledger.Domain;
using Decisya.Modules.Ledger.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// Creates an account and enforces the limit of <see cref="AccountLimits.PerSpace"/> in the same transaction
/// (issue #341, G2 "The two-account limit"; G3 M341-02). The steps, with the default isolation level (READ
/// COMMITTED) and no retry: ensure the space's <see cref="AccountBook"/> row, take its row lock with an UPDATE,
/// count the accounts (the count starts after the lock was granted, so it sees every account committed by the
/// creates that held the lock before), refuse at the limit, add the account and its audit record, commit.
/// A refusal disposes the transaction uncommitted: no account, no audit record, and the lock increment rolled back.
/// </summary>
internal sealed class CreateAccountHandler(
    LedgerDbContext db,
    ICurrentTenant tenant,
    IAuditWriter audit,
    IClock clock,
    ILogger<CreateAccountHandler> logger)
{
    private const string BookTenantIndex = "ux_account_books_tenant";

    public async Task<AccountCommandResult> HandleAsync(CreateAccount command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Defence in depth: the policy has already refused. Nothing below runs, and no database access.
        if (tenant.Resolution.Kind != TenantResolutionKind.Tenant)
        {
            LedgerWriteReport.Refused(logger, null, LedgerTelemetry.OperationCreate, LedgerTelemetry.ReasonForbidden);
            return new AccountCommandResult(LedgerOutcome.Forbidden, null);
        }

        using var span = LedgerTelemetry.StartSpan("Ledger.CreateAccount", tenant);

        AccountCommandResult result;
        try
        {
            result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LedgerWriteReport.Failed(span, LedgerTelemetry.OperationCreate);
            throw;
        }

        LedgerWriteReport.Completed(logger, span, LedgerTelemetry.OperationCreate, result);
        return result;
    }

    private async Task<AccountCommandResult> ExecuteAsync(CreateAccount command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Resolution.TenantId;
        var now = clock.GetCurrentInstant();

        // ADR-0013: the account and its audit record commit together or not at all. An early return or any
        // exception leaves the transaction uncommitted, and disposal rolls it back.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await EnsureBookAsync(tenantId, now, cancellationToken).ConfigureAwait(false);

        var locked = await LockBookAsync(cancellationToken).ConfigureAwait(false);
        if (locked != 1)
        {
            throw new InvalidOperationException("The account book lock did not match exactly one row.");
        }

        var used = await db.Accounts.CountAsync(cancellationToken).ConfigureAwait(false);
        if (used >= AccountLimits.PerSpace)
        {
            return new AccountCommandResult(LedgerOutcome.LimitReached, null);
        }

        var account = Account.Open(tenantId, command.Name, command.Currency, command.OpeningBalance, command.OpeningDate, now);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await audit.AppendAsync(
            new AuditEntry(tenantId, AuditAction.LedgerAccountCreate, null, account.Id),
            tx.GetDbTransaction(),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AccountCommandResult(LedgerOutcome.Succeeded, AccountMapper.ToDto(account, default));
    }

    /// <summary>
    /// Makes sure the space has its book row. A concurrent first create may insert it first: Postgres raises the
    /// 23505 on the unique index only after the winner commits, and EF's savepoint has rolled this insert back, so
    /// the tracker is cleared and the lock statement below finds the winner's row. Any other failure propagates.
    /// </summary>
    private async Task EnsureBookAsync(TenantId tenantId, Instant now, CancellationToken cancellationToken)
    {
        if (await db.AccountBooks.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        db.AccountBooks.Add(AccountBook.Open(tenantId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex, BookTenantIndex))
        {
            db.ChangeTracker.Clear();
        }
    }

    // G3 M341-02: the only bulk statement in the module. The tenant filter limits it to the space's own row,
    // and the UPDATE holds that row's lock until commit or rollback, so a second create in the same space waits
    // here. Kept in its own method with no TenantId expression, so BulkTenantMoveRule never sees SetProperty next to one.
    private Task<int> LockBookAsync(CancellationToken cancellationToken) =>
        db.AccountBooks.ExecuteUpdateAsync(
            setters => setters.SetProperty(book => book.LockVersion, book => book.LockVersion + 1),
            cancellationToken);
}
