// See MoneyTests.cs for why this is "Decisya.SharedKernel.Tests", not "...Tests.Money".
namespace Decisya.SharedKernel.Tests;

/// <summary>
/// NFR-08 — Money.Allocate output sums to exactly the input amount (zero-drift) across
/// >= 10,000 randomized property-based cases.
/// </summary>
public class MoneyAllocationPropertyTests
{
    private static readonly string[] CurrencyCodes = ["USD", "EUR", "JPY", "BHD"];

    [Fact]
    public void Allocate_never_drifts_from_the_original_amount_across_ten_thousand_random_cases()
    {
        // Fixed seed: failures are reproducible, not flaky.
        var random = new Random(20260415);

        for (var i = 0; i < 10_000; i++)
        {
            var currency = Currency.FromCode(CurrencyCodes[random.Next(CurrencyCodes.Length)]);
            var money = new Money(RandomAmount(random, currency), currency);
            var partCount = random.Next(1, 8);

            var parts = random.Next(2) == 0
                ? money.Allocate(partCount)
                : money.Allocate(RandomRatios(random, partCount));

            parts.Should().HaveCount(partCount);
            parts.Aggregate((a, b) => a + b).Should()
                .Be(money, "case {0}: allocating {1} into {2} parts must never gain or lose minor units", i, money, partCount);
        }
    }

    /// <summary>
    /// Security review 0.04, N1 — Allocate(-x) must be the exact, share-for-share
    /// negation of Allocate(x) for the same ratios: a refund's per-party split always
    /// cancels the original charge's per-party split, not merely the totals.
    /// </summary>
    [Fact]
    public void Allocate_of_a_negative_amount_mirrors_allocate_of_the_positive_amount_across_ten_thousand_random_cases()
    {
        // Fixed seed: failures are reproducible, not flaky.
        var random = new Random(20260923);

        for (var i = 0; i < 10_000; i++)
        {
            var currency = Currency.FromCode(CurrencyCodes[random.Next(CurrencyCodes.Length)]);
            var amount = RandomAmount(random, currency);
            var positive = new Money(Math.Abs(amount), currency);
            var negative = new Money(-Math.Abs(amount), currency);
            var partCount = random.Next(1, 8);
            var ratios = RandomRatios(random, partCount);

            var positiveParts = positive.Allocate(ratios);
            var negativeParts = negative.Allocate(ratios);

            negativeParts.Select(p => p.Amount).Should().Equal(
                positiveParts.Select(p => -p.Amount),
                "case {0}: allocating -{1} by the same ratios as {1} must produce the exact " +
                "element-wise negation, including any midpoint tie-break", i, positive);
        }
    }

    [Fact]
    public void Allocate_of_a_negative_amount_mirrors_the_positive_allocation_at_an_exact_midpoint_tie_break()
    {
        // 0.05 USD split [1, 1] lands exactly on the banker's-rounding midpoint (see
        // MoneyAllocationTests) — the case most likely to break under a careless fix.
        var usd = Currency.FromCode("USD");
        var positive = new Money(0.05m, usd);
        var negative = new Money(-0.05m, usd);

        var positiveParts = positive.Allocate([1, 1]);
        var negativeParts = negative.Allocate([1, 1]);

        negativeParts.Select(p => p.Amount).Should().Equal(positiveParts.Select(p => -p.Amount));
    }

    private static decimal RandomAmount(Random random, Currency currency)
    {
        var minorUnits = random.Next(-1_000_000, 1_000_000);
        return minorUnits / Pow10(currency.MinorUnitExponent);
    }

    private static int[] RandomRatios(Random random, int count)
    {
        var ratios = new int[count];
        for (var i = 0; i < count; i++)
        {
            ratios[i] = random.Next(1, 10);
        }

        return ratios;
    }

    private static decimal Pow10(int exponent) => exponent switch
    {
        0 => 1m,
        1 => 10m,
        2 => 100m,
        3 => 1000m,
        4 => 10000m,
        _ => throw new ArgumentOutOfRangeException(nameof(exponent), exponent, null),
    };
}
