// See MoneyTests.cs for why this is "Decisya.SharedKernel.Tests", not "...Tests.Money".
namespace Decisya.SharedKernel.Tests;

/// <summary>Story 3 — Deterministic allocation and rounding of Money.</summary>
public class MoneyAllocationTests
{
    private static readonly Currency Usd = Currency.FromCode("USD");
    private static readonly Currency Jpy = Currency.FromCode("JPY");

    [Fact]
    public void Equal_three_way_split_of_an_amount_that_doesnt_divide_evenly()
    {
        var money = new Money(100.00m, Usd);

        var parts = money.Allocate(3);

        parts.Should().Equal(
            new Money(33.34m, Usd),
            new Money(33.33m, Usd),
            new Money(33.33m, Usd));
        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Weighted_ratio_allocation_sums_back_to_the_original_amount()
    {
        var money = new Money(100.00m, Usd);

        var parts = money.Allocate([1, 1, 2]);

        parts.Aggregate((a, b) => a + b).Should().Be(money);

        // Proportional to the ratio, within one minor unit (ideal: 25.00, 25.00, 50.00).
        parts[0].Amount.Should().BeApproximately(25.00m, 0.01m);
        parts[1].Amount.Should().BeApproximately(25.00m, 0.01m);
        parts[2].Amount.Should().BeApproximately(50.00m, 0.01m);
    }

    [Fact]
    public void Allocation_on_a_zero_exponent_currency_never_produces_fractional_minor_units()
    {
        var money = new Money(100m, Jpy);

        var parts = money.Allocate(3);

        parts.Should().OnlyContain(p => p.Amount == decimal.Truncate(p.Amount));
        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Allocation_rejects_invalid_ratios()
    {
        var money = new Money(100.00m, Usd);

        var act = () => money.Allocate([0, 0]);

        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void Allocation_rejects_a_non_positive_part_count()
    {
        var money = new Money(100.00m, Usd);

        var act = () => money.Allocate(0);

        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void Rounding_at_an_exact_midpoint_is_deterministic_banker_rounding()
    {
        // 6 minor units split [1, 3]: idx0 raw share 1.5 (floor 1, odd) -> rounds up to
        // the even value 2; idx1 raw share 4.5 (floor 4, even) -> stays at the even
        // value 4. Both individual midpoints resolve to the nearest EVEN minor unit,
        // and the shares still sum to exactly the original amount.
        var money = new Money(0.06m, Usd);

        var parts = money.Allocate([1, 3]);

        parts.Should().Equal(new Money(0.02m, Usd), new Money(0.04m, Usd));
        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Repeating_the_midpoint_allocation_produces_the_identical_result_every_time()
    {
        var money = new Money(0.06m, Usd);

        var first = money.Allocate([1, 3]);
        var second = money.Allocate([1, 3]);

        second.Should().Equal(first);
    }

    [Fact]
    public void Negative_three_way_split_is_the_mirror_image_of_the_positive_split()
    {
        // Security review 0.04, N1: a refund's per-party split must cancel the original
        // charge's per-party split exactly, not merely sum to the same (negative) total.
        var money = new Money(-100.00m, Usd);

        var parts = money.Allocate(3);

        parts.Should().Equal(
            new Money(-33.34m, Usd),
            new Money(-33.33m, Usd),
            new Money(-33.33m, Usd));
        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Negative_amount_at_an_exact_midpoint_mirrors_the_positive_banker_rounded_split()
    {
        // Positive 0.05 USD split [1, 1] lands exactly on the banker's-rounding midpoint
        // and resolves to [0.03, 0.02] (see Rounding_at_an_exact_midpoint_is_deterministic_banker_rounding);
        // the negative amount must mirror that exactly, tie-break included.
        var money = new Money(-0.05m, Usd);

        var parts = money.Allocate([1, 1]);

        parts.Should().Equal(new Money(-0.03m, Usd), new Money(-0.02m, Usd));
        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }
}
