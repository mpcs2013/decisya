using Decisya.Modules.Ledger.Contracts;
using Decisya.Modules.Ledger.Domain;
using Decisya.Modules.Ledger.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// The two reads (issue #341, G1 Story 2): the space's accounts and one account by id. Both go through
/// <see cref="LedgerDbContext"/>'s tenant filter, so another space's id is as invisible as a missing one. The
/// balance and the lock fact come from <see cref="IAccountActivity"/>, called once per read.
/// </summary>
internal sealed class AccountQueries(
    LedgerDbContext db, IAccountActivity activity, ICurrentTenant tenant, ILogger<AccountQueries> logger)
{
    public async Task<AccountListResponse> ListAsync(CancellationToken cancellationToken)
    {
        using var span = LedgerTelemetry.StartSpan("Ledger.ListAccounts", tenant);

        try
        {
            var accounts = await db.Accounts
                .AsNoTracking()
                .OrderBy(a => a.CreatedAt)
                .ThenBy(a => a.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var summaries = await activity
                .SummarizeAsync(accounts.ConvertAll(a => a.Id), cancellationToken)
                .ConfigureAwait(false);

            var dtos = new List<AccountDto>(accounts.Count);
            foreach (var account in accounts)
            {
                dtos.Add(AccountMapper.ToDto(account, summaries.GetValueOrDefault(account.Id)));
            }

            LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultOk);
            LedgerTelemetry.Record(LedgerTelemetry.OperationList, LedgerTelemetry.ResultOk, LedgerTelemetry.ReasonNone);

            return new AccountListResponse(dtos, new AccountUsageDto(dtos.Count, AccountLimits.PerSpace));
        }
        catch (Exception)
        {
            LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultError);
            LedgerTelemetry.Record(LedgerTelemetry.OperationList, LedgerTelemetry.ResultError, LedgerTelemetry.ReasonNone);
            throw;
        }
    }

    /// <summary>The account, or <see langword="null"/> when it is missing or belongs to another space (the two cannot be told apart).</summary>
    public async Task<AccountDto?> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        using var span = LedgerTelemetry.StartSpan("Ledger.GetAccount", tenant);

        try
        {
            var account = await db.Accounts
                .AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken)
                .ConfigureAwait(false);

            if (account is null)
            {
                LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultRefused);
                LedgerTelemetry.Record(LedgerTelemetry.OperationGet, LedgerTelemetry.ResultRefused, LedgerTelemetry.ReasonNotFound);
                LedgerLog.Refused(logger, LedgerTelemetry.OperationGet, LedgerTelemetry.ResultRefused, LedgerTelemetry.ReasonNotFound);
                return null;
            }

            var summaries = await activity
                .SummarizeAsync([account.Id], cancellationToken)
                .ConfigureAwait(false);

            LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultOk);
            LedgerTelemetry.Record(LedgerTelemetry.OperationGet, LedgerTelemetry.ResultOk, LedgerTelemetry.ReasonNone);

            return AccountMapper.ToDto(account, summaries.GetValueOrDefault(account.Id));
        }
        catch (Exception)
        {
            LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultError);
            LedgerTelemetry.Record(LedgerTelemetry.OperationGet, LedgerTelemetry.ResultError, LedgerTelemetry.ReasonNone);
            throw;
        }
    }
}
