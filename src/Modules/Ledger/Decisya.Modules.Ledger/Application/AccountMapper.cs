using Decisya.Modules.Ledger.Contracts;
using Decisya.Modules.Ledger.Domain;
using Decisya.SharedKernel;
using NodaTime.Text;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// Maps an account to the wire (ADR-0027). The balance is never clipped or rounded: a value outside the
/// JavaScript-safe range, or one that does not fit a <see cref="long"/>, is a defect and fails the request as the
/// generic 500 (ADR-0027 point 2). Every exception here carries a fixed message with no value.
/// </summary>
internal static class AccountMapper
{
    public static AccountDto ToDto(Account account, AccountActivitySummary activity) =>
        new(
            account.Id,
            account.Name,
            account.Currency.Code,
            ToMoneyDto(account.OpeningBalance),
            account.OpeningDate is { } date ? LocalDatePattern.Iso.Format(date) : null,
            ToMoneyDto(account.BalanceWith(activity.NetMinor)),
            activity.HoldsTransaction);

    private static MoneyDto ToMoneyDto(Money money)
    {
        var minor = money.ToMinorUnits();
        if (minor is < MoneyDto.MinAmountMinor or > MoneyDto.MaxAmountMinor)
        {
            throw new InvalidOperationException("An amount left the wire range of ADR-0027.");
        }

        return new MoneyDto(minor, money.Currency.Code);
    }
}
