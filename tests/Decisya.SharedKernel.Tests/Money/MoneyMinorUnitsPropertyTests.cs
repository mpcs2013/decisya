// See MoneyTests.cs for why this is "Decisya.SharedKernel.Tests", not "...Tests.Money".
namespace Decisya.SharedKernel.Tests;

/// <summary>
/// NFR-82, ADR-0027 point 5 and "Enforced by": <c>Money.FromMinorUnits</c> and <c>Money.ToMinorUnits</c> are the
/// exact conversion between the wire form (an integer count of minor units within plus or minus 2^53 - 1, in the
/// currency's own ISO 4217 exponent) and <see cref="Money"/>. At least 10 000 seeded, deterministic cases across the
/// exponents 0, 2, 3 and 4 round-trip in both directions without losing or gaining a minor unit, plus the point 5
/// exceptions. No property-testing package: a fixed seed makes a failure reproducible, not flaky (as
/// <c>MoneyAllocationPropertyTests</c>).
/// </summary>
[Trait("Category", "Unit")]
public class MoneyMinorUnitsPropertyTests
{
    private const long MaxWire = 9_007_199_254_740_991; // 2^53 - 1 (ADR-0027 point 2)

    // JPY 0, EUR 2, KWD 3, CLF 4 (ADR-0027 point 1).
    private static readonly string[] CurrencyCodes = ["JPY", "EUR", "KWD", "CLF"];

    private static readonly long[] EdgeCases =
    [
        0, 1, -1, 9, -9, 10, 99, 100, 101, 999, 1_000, 9_999, 10_000, int.MaxValue, int.MinValue, uint.MaxValue,
        MaxWire, -MaxWire, MaxWire - 1, -MaxWire + 1, 1L << 52, -(1L << 52),
    ];

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    [Fact]
    public void The_four_currencies_cover_the_exponents_0_2_3_and_4()
    {
        CurrencyCodes.Select(code => Currency.FromCode(code).MinorUnitExponent).Should().Equal(0, 2, 3, 4);
    }

    [Fact]
    public void FromMinorUnits_and_ToMinorUnits_round_trip_exactly_across_ten_thousand_seeded_cases_and_every_exponent()
    {
        // Fixed seed: failures are reproducible, not flaky.
        var random = new Random(20261122);
        var cases = 0;

        for (var i = 0; i < 10_000; i++)
        {
            var currency = Currency.FromCode(CurrencyCodes[i % CurrencyCodes.Length]);
            var minor = i < EdgeCases.Length * CurrencyCodes.Length
                ? EdgeCases[i / CurrencyCodes.Length]
                : random.NextInt64(-MaxWire, MaxWire + 1);

            // Wire to Money to wire: the same integer, in the currency's own exponent.
            var money = Money.FromMinorUnits(minor, currency);
            money.Currency.Should().Be(currency, "case {0}", i);
            money.Amount.Should().Be((decimal)minor / Pow10(currency.MinorUnitExponent), "case {0}: {1} minor units of {2}", i, minor, currency.Code);
            money.ToMinorUnits().Should().Be(minor, "case {0}: {1} minor units of {2} must survive the round trip", i, minor, currency.Code);

            // Money to wire to Money: a major-unit amount with at most the currency's digits, in the same range.
            var original = new Money(RandomAmount(random, currency), currency);
            var wire = original.ToMinorUnits();
            wire.Should().BeInRange(-MaxWire, MaxWire, "case {0}", i);
            Money.FromMinorUnits(wire, currency).Should().Be(original, "case {0}: {1} must survive the round trip through {2} minor units", i, original, wire);

            cases++;
        }

        cases.Should().BeGreaterThanOrEqualTo(10_000);
    }

    [Fact]
    public void The_exponent_scales_the_wire_amount_by_exactly_a_power_of_ten()
    {
        Money.FromMinorUnits(125_000, Currency.FromCode("EUR")).Amount.Should().Be(1250.00m);
        Money.FromMinorUnits(5_000, Currency.FromCode("JPY")).Amount.Should().Be(5000m);
        Money.FromMinorUnits(1_500_000, Currency.FromCode("KWD")).Amount.Should().Be(1500.000m);
        Money.FromMinorUnits(12_345_678, Currency.FromCode("CLF")).Amount.Should().Be(1234.5678m);
        Money.FromMinorUnits(-1, Currency.FromCode("KWD")).Amount.Should().Be(-0.001m);
    }

    // ---- ADR-0027 point 5: the exceptions ----

    [Fact]
    public void FromMinorUnits_refuses_default_Currency_with_an_ArgumentException()
    {
        var act = () => Money.FromMinorUnits(100, default);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToMinorUnits_of_default_Money_throws_InvalidOperationException()
    {
        var act = () => default(Money).ToMinorUnits();

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("JPY", "9300000000000000000")]
    [InlineData("EUR", "93000000000000000")]
    [InlineData("KWD", "9300000000000000")]
    [InlineData("CLF", "930000000000000")]
    [InlineData("EUR", "-93000000000000000")]
    public void ToMinorUnits_throws_OverflowException_when_the_amount_does_not_fit_a_long(string code, string majorUnits)
    {
        var money = new Money(decimal.Parse(majorUnits, System.Globalization.CultureInfo.InvariantCulture), Currency.FromCode(code));

        var act = () => money.ToMinorUnits();

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void The_whole_long_range_converts_exactly_in_both_directions_for_the_widest_exponent_that_fits()
    {
        var jpy = Currency.FromCode("JPY");

        Money.FromMinorUnits(long.MaxValue, jpy).ToMinorUnits().Should().Be(long.MaxValue);
        Money.FromMinorUnits(long.MinValue, jpy).ToMinorUnits().Should().Be(long.MinValue);
    }

    private static decimal RandomAmount(Random random, Currency currency)
    {
        var minor = random.NextInt64(-MaxWire, MaxWire + 1);
        return (decimal)minor / Pow10(currency.MinorUnitExponent);
    }
}
