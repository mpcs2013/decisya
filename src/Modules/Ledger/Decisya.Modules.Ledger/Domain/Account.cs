using Decisya.SharedKernel;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Ledger.Domain;

/// <summary>
/// One account of a space (issue #341, G2). The money is stored as <c>opening_balance_minor</c> (bigint minor
/// units) and <c>currency</c> (char(3)); <see cref="OpeningBalance"/> is computed, never mapped. There is no
/// IBAN, card or account-number member anywhere (G1 Story 8). The tenant is set by the factory only.
/// </summary>
internal sealed class Account : ITenantScoped
{
    private Account()
    {
        // EF Core materialization constructor.
    }

    private Account(TenantId tenantId, AccountName name, Currency currency, long openingBalanceMinor, LocalDate? openingDate, Instant now)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = name.Value;
        Currency = currency;
        OpeningBalanceMinor = openingBalanceMinor;
        OpeningDate = openingDate;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    [Sensitive]
    public string Name { get; private set; } = string.Empty;

    public Currency Currency { get; private set; }

    public long OpeningBalanceMinor { get; private set; }

    public LocalDate? OpeningDate { get; private set; }

    public Instant CreatedAt { get; private set; }

    /// <summary>The opening balance in the account's currency (zero when none was given). Not mapped.</summary>
    public Money OpeningBalance => Money.FromMinorUnits(OpeningBalanceMinor, Currency);

    /// <summary>
    /// Opens an account. The endpoint has already refused a currency mismatch and a broken opening pair, so
    /// either here is a programming error (a fixed message, no value).
    /// </summary>
    /// <exception cref="ArgumentException">The name or currency is uninitialized, the balance is in another currency, or exactly one of balance and date is given.</exception>
    public static Account Open(TenantId tenantId, AccountName name, Currency currency, Money? openingBalance, LocalDate? openingDate, Instant now)
    {
        if (name.Value is null)
        {
            throw new ArgumentException("The account name is not initialized.", nameof(name));
        }

        var minor = OpeningMinor(currency, openingBalance, openingDate);
        return new Account(tenantId, name, currency, minor, openingDate, now);
    }

    /// <summary>Renames the account. The name keeps the exact characters it was given.</summary>
    public void Rename(AccountName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException("The account name is not initialized.", nameof(name));
        }

        Name = name.Value;
    }

    /// <summary>
    /// Changes the currency and restates the opening balance and date. Returns <see langword="false"/> and
    /// changes nothing when the account holds a transaction (the currency is locked, A3).
    /// </summary>
    public bool TryChangeCurrency(Currency currency, Money? openingBalance, LocalDate? openingDate, bool holdsTransaction)
    {
        if (holdsTransaction)
        {
            return false;
        }

        var minor = OpeningMinor(currency, openingBalance, openingDate);
        Currency = currency;
        OpeningBalanceMinor = minor;
        OpeningDate = openingDate;
        return true;
    }

    /// <summary>The opening balance plus the net of the account's transactions, both in the account's currency.</summary>
    public Money BalanceWith(long netTransactionsMinor) =>
        OpeningBalance + Money.FromMinorUnits(netTransactionsMinor, Currency);

    private static long OpeningMinor(Currency currency, Money? openingBalance, LocalDate? openingDate)
    {
        if (!currency.IsInitialized)
        {
            throw new ArgumentException("The account currency is not initialized.", nameof(currency));
        }

        if (openingBalance.HasValue != openingDate.HasValue)
        {
            throw new ArgumentException("The opening balance and the opening date come together or not at all.", nameof(openingBalance));
        }

        if (openingBalance is not { } balance)
        {
            return 0;
        }

        if (balance.Currency != currency)
        {
            throw new ArgumentException("The opening balance is not in the account's currency.", nameof(openingBalance));
        }

        return balance.ToMinorUnits();
    }
}
