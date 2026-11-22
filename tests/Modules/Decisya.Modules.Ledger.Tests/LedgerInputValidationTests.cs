using Decisya.Modules.Ledger.Contracts;
using Decisya.Modules.Ledger.Domain;
using Decisya.Modules.Ledger.Endpoints;
using Decisya.SharedKernel;
using NodaTime;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G1 Story 1, scenario outline "Invalid input is refused with a generic validation error and nothing is stored"
/// (AC-02-1), at the layer that decides it: the shape checks the endpoint runs after parsing and before any
/// database access (<see cref="AccountName"/> and <see cref="AccountInput"/>). The HTTP half (status, generic body,
/// nothing stored, no audit record) is <c>Decisya.Api.Tests.Authentication.LedgerEndpointPostgresTests</c>.
/// Also G1 Story 5, scenario "A rename with an invalid name is refused". No database, no host.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LedgerInputValidationTests
{
    private static readonly Currency Eur = Currency.FromCode("EUR");

    private static string Chars(int count) => new('x', count);

    // ---- the name (A1) ----

    [Theory]
    [InlineData("Everyday", "Everyday")]
    [InlineData("  Everyday  ", "Everyday")]
    [InlineData("<script>alert(1)</script>", "<script>alert(1)</script>")]
    [InlineData("Café Épargne", "Café Épargne")]
    public void A_valid_name_is_trimmed_and_otherwise_kept_exactly_as_typed(string raw, string expected)
    {
        AccountName.TryCreate(raw, out var name).Should().BeTrue();

        name.Value.Should().Be(expected);
    }

    [Fact]
    public void A_name_of_80_characters_is_accepted_and_a_name_of_81_characters_is_refused()
    {
        AccountName.TryCreate(Chars(80), out var accepted).Should().BeTrue();
        accepted.Value.Should().HaveLength(80);

        AccountName.TryCreate(Chars(81), out _).Should().BeFalse();
    }

    [Fact]
    public void The_limit_counts_Unicode_scalar_values_not_UTF16_code_units()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);

        AccountName.TryCreate(string.Concat(Enumerable.Repeat(emoji, 80)), out _).Should().BeTrue();
        AccountName.TryCreate(string.Concat(Enumerable.Repeat(emoji, 81)), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    [InlineData("line\nbreak")]
    [InlineData("\uD800")]
    [InlineData("\uDC00x")]
    public void An_empty_blank_null_control_or_lone_surrogate_name_is_refused(string? raw)
    {
        AccountName.TryCreate(raw, out var name).Should().BeFalse();

        name.Value.Should().BeNull("a refused name is default and carries nothing");
    }

    // ---- the currency ----

    [Theory]
    [InlineData("EUR")]
    [InlineData("JPY")]
    [InlineData("KWD")]
    [InlineData("CHF")]
    public void An_upper_case_ISO_4217_code_is_read(string code)
    {
        AccountInput.TryReadCurrency(code, out var currency).Should().BeTrue();

        currency.Code.Should().Be(code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EURO")]
    [InlineData("EU")]
    [InlineData("XXX")]
    [InlineData("ZZZ")]
    [InlineData("eur")]
    [InlineData("Eur")]
    [InlineData(" EUR")]
    [InlineData("EUR ")]
    [InlineData("EU1")]
    public void A_currency_that_is_not_three_upper_case_letters_or_not_in_ISO_4217_is_refused(string? code)
    {
        AccountInput.TryReadCurrency(code, out _).Should().BeFalse();
    }

    // ---- the opening balance and its date (A2, A4) ----

    [Fact]
    public void No_opening_balance_and_no_date_is_valid_and_means_none()
    {
        var error = AccountInput.ReadOpening(Eur, null, null, out var balance, out var date);

        error.Should().BeNull();
        balance.Should().BeNull();
        date.Should().BeNull();
    }

    [Theory]
    [InlineData(125000L, "EUR", "2026-12-01")]
    [InlineData(-4500L, "EUR", "2026-12-01")]
    [InlineData(0L, "EUR", "2026-12-01")]
    [InlineData(MoneyDto.MaxAmountMinor, "EUR", "2026-12-01")]
    [InlineData(MoneyDto.MinAmountMinor, "EUR", "2026-12-01")]
    public void An_opening_balance_in_range_with_its_date_is_read_in_minor_units(long minor, string currency, string text)
    {
        var error = AccountInput.ReadOpening(Eur, new MoneyDto(minor, currency), text, out var balance, out var date);

        error.Should().BeNull();
        balance!.Value.ToMinorUnits().Should().Be(minor);
        balance.Value.Currency.Code.Should().Be("EUR");
        date.Should().Be(new LocalDate(2026, 12, 1));
    }

    [Theory]
    [InlineData(MoneyDto.MaxAmountMinor + 1)]
    [InlineData(MoneyDto.MinAmountMinor - 1)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void An_opening_balance_outside_the_wire_range_is_refused(long minor)
    {
        var error = AccountInput.ReadOpening(Eur, new MoneyDto(minor, "EUR"), "2026-12-01", out var balance, out _);

        error.Should().Be(LedgerErrorCodes.AmountInvalid);
        balance.Should().BeNull();
    }

    [Fact]
    public void An_opening_balance_with_no_date_is_refused()
    {
        AccountInput.ReadOpening(Eur, new MoneyDto(100, "EUR"), null, out _, out _)
            .Should().Be(LedgerErrorCodes.OpeningPairInvalid);
    }

    [Fact]
    public void An_opening_date_with_no_opening_balance_is_refused()
    {
        AccountInput.ReadOpening(Eur, null, "2026-12-01", out _, out _)
            .Should().Be(LedgerErrorCodes.OpeningPairInvalid);
    }

    [Fact]
    public void An_opening_balance_in_another_currency_than_the_account_is_refused()
    {
        AccountInput.ReadOpening(Eur, new MoneyDto(100, "USD"), "2026-12-01", out _, out _)
            .Should().Be(LedgerErrorCodes.CurrencyMismatch);
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("XXX")]
    public void An_opening_balance_with_an_invalid_currency_code_is_refused(string code)
    {
        AccountInput.ReadOpening(Eur, new MoneyDto(100, code), "2026-12-01", out _, out _)
            .Should().Be(LedgerErrorCodes.CurrencyInvalid);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("01/12/2026")]
    [InlineData("2026-1-1")]
    [InlineData("20261201")]
    [InlineData("2026-12-01T00:00:00Z")]
    [InlineData("not a date")]
    [InlineData("")]
    public void An_opening_date_that_is_not_a_strict_ISO_calendar_date_is_refused(string text)
    {
        AccountInput.ReadOpening(Eur, new MoneyDto(100, "EUR"), text, out _, out _)
            .Should().Be(LedgerErrorCodes.OpeningDateInvalid);
    }

    [Theory]
    [InlineData("JPY", 5000L)]
    [InlineData("EUR", 125000L)]
    [InlineData("KWD", 1500000L)]
    public void Minor_units_follow_the_currencys_own_exponent(string code, long minor)
    {
        var currency = Currency.FromCode(code);

        var error = AccountInput.ReadOpening(currency, new MoneyDto(minor, code), "2026-12-01", out var balance, out _);

        error.Should().BeNull();
        balance!.Value.ToMinorUnits().Should().Be(minor, "the balance reads back as exactly the minor units given");
        balance.Value.Currency.Should().Be(currency);
    }
}
