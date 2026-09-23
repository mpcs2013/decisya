// Deliberately not "...Tests.Money": a namespace segment literally named "Money" would
// shadow the Decisya.SharedKernel.Money class for every unqualified reference in this
// file (CS0118, "'Money' is a namespace but is used like a type").
namespace Decisya.SharedKernel.Tests;

/// <summary>Story 2 — Money is a same-currency-safe value object.</summary>
public class MoneyTests
{
    private static readonly Currency Usd = Currency.FromCode("USD");
    private static readonly Currency Eur = Currency.FromCode("EUR");

    [Fact]
    public void Constructing_Money_at_exactly_the_currencys_precision_succeeds()
    {
        var money = new Money(100.50m, Usd);

        money.Amount.Should().Be(100.50m);
        money.Currency.Should().Be(Usd);
    }

    [Fact]
    public void Constructing_Money_with_excess_precision_is_rejected()
    {
        var act = () => new Money(100.505m, Usd);

        var exception = act.Should().Throw<MoneyPrecisionException>().Which;
        exception.Amount.Should().Be(100.505m);
        exception.Currency.Should().Be(Usd);
    }

    [Fact]
    public void MoneyPrecisionExceptions_message_names_the_currency_and_exponent_but_never_the_amount()
    {
        var act = () => new Money(100.505m, Usd);

        var exception = act.Should().Throw<MoneyPrecisionException>().Which;
        exception.Message.Should().Contain("USD");
        exception.Message.Should().Contain("2");
        exception.Message.Should().NotContain("100.505");
        exception.Amount.Should().Be(100.505m);
    }

    [Fact]
    public void Constructing_Money_with_an_uninitialised_default_currency_is_rejected()
    {
        var act = () => new Money(5m, default);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Default_Money_ToString_formats_as_exactly_zero_with_no_trailing_space()
    {
        // default(Money) bypasses the constructor entirely (it is a struct); an
        // unhandled exception here would fail this test, which is the point. Security
        // review 0.04, N3: the exact string must match the XML doc, not "0 " with a
        // trailing space left over from the (absent) currency code.
        var text = default(Money).ToString();

        text.Should().Be("0");
    }

    [Fact]
    public void CurrencyMismatchException_message_does_not_throw_for_a_default_currency()
    {
        // An unhandled exception constructing this would fail the test, which is the
        // point: the exception's own constructor must never read Currency.Code (which
        // throws for a default, uninitialised Currency).
        var exception = new CurrencyMismatchException(default, Usd);

        exception.Message.Should().Contain("USD");
    }

    [Fact]
    public void MoneyPrecisionException_message_does_not_throw_for_a_default_currency()
    {
        // Security review 0.04, N4: the exception's own constructor must never read
        // Currency.Code (which throws for a default, uninitialised Currency) — it must
        // use Currency.ToString() instead, like CurrencyMismatchException. An unhandled
        // exception constructing this would fail the test, which is the point.
        var exception = new MoneyPrecisionException(1m, default);

        exception.Message.Should().NotBeNullOrEmpty();
        exception.Amount.Should().Be(1m);
    }

    [Fact]
    public void Adding_two_Money_values_of_the_same_currency_succeeds()
    {
        var ten = new Money(10.00m, Usd);
        var fiveTwentyFive = new Money(5.25m, Usd);

        var result = ten + fiveTwentyFive;

        result.Should().Be(new Money(15.25m, Usd));
    }

    [Fact]
    public void Adding_Money_values_of_different_currencies_is_rejected()
    {
        var ten = new Money(10.00m, Usd);
        var fiveTwentyFive = new Money(5.25m, Eur);

        var act = () => ten + fiveTwentyFive;

        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Subtraction_below_zero_is_allowed()
    {
        var twentyFive = new Money(25.00m, Usd);
        var ten = new Money(10.00m, Usd);

        var result = twentyFive - ten;

        result.Should().Be(new Money(15.00m, Usd));

        var negative = ten - twentyFive;
        negative.Amount.Should().Be(-15.00m);
    }

    [Fact]
    public void Comparison_operators_require_the_same_currency()
    {
        var tenUsd = new Money(10.00m, Usd);
        var tenEur = new Money(10.00m, Eur);

        var less = () => tenUsd < tenEur;
        var greater = () => tenUsd > tenEur;

        less.Should().Throw<CurrencyMismatchException>();
        greater.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Comparison_operators_compare_amounts_of_the_same_currency()
    {
        var five = new Money(5.00m, Usd);
        var fiveAgain = new Money(5.00m, Usd);
        var ten = new Money(10.00m, Usd);
        var tenAgain = new Money(10.00m, Usd);

        (five < ten).Should().BeTrue();
        (ten > five).Should().BeTrue();
        (five <= fiveAgain).Should().BeTrue();
        (ten >= tenAgain).Should().BeTrue();
    }

    [Fact]
    public void Money_formats_using_the_currencys_exponent()
    {
        var money = new Money(100.00m, Usd);

        money.ToString().Should().Be("100.00 USD");
    }

    [Fact]
    public void Money_formats_a_zero_exponent_currency_with_no_decimal_places()
    {
        var jpy = Currency.FromCode("JPY");
        var money = new Money(100m, jpy);

        money.ToString().Should().Be("100 JPY");
    }

    [Fact]
    public void Money_formats_a_four_decimal_currency_with_four_decimal_places()
    {
        var clf = Currency.FromCode("CLF");
        var money = new Money(1.2345m, clf);

        money.ToString().Should().Be("1.2345 CLF");
    }

    [Fact]
    public void Unary_negation_flips_the_sign_and_keeps_the_currency()
    {
        var ten = new Money(10.00m, Usd);

        var negated = -ten;

        negated.Should().Be(new Money(-10.00m, Usd));
    }

    [Fact]
    public void Inequality_operator_is_the_negation_of_equality()
    {
        var ten = new Money(10.00m, Usd);
        var tenEur = new Money(10.00m, Eur);

        (ten != tenEur).Should().BeTrue();
    }

    [Fact]
    public void Money_is_a_value_type_with_structural_equality()
    {
        var first = new Money(42.00m, Usd);
        var second = new Money(42.00m, Usd);

        first.Should().Be(second);
        (first == second).Should().BeTrue();
        first.GetHashCode().Should().Be(second.GetHashCode());
    }
}
