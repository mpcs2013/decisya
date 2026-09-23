// See MoneyTests.cs for why this is "Decisya.SharedKernel.Tests", not "...Tests.Money".
namespace Decisya.SharedKernel.Tests;

/// <summary>
/// Security review 0.04, M1/M2 — <see cref="Money.Allocate(int)"/> and
/// <see cref="Money.Allocate(IReadOnlyList{int})"/> must never overflow their internal
/// arithmetic (large amounts / large ratios), never accept an unbounded part count, and
/// must fail with <see cref="InvalidAllocationException"/> — never an incidental
/// <see cref="IndexOutOfRangeException"/> or <see cref="OverflowException"/> — when asked
/// to do something outside their documented range.
/// </summary>
public class MoneyAllocationBoundaryTests
{
    private static readonly Currency Usd = Currency.FromCode("USD");
    private static readonly Currency Vnd = Currency.FromCode("VND");

    [Fact]
    public void Allocating_50_million_USD_by_int_MaxValue_and_1_sums_to_exactly_the_original_amount()
    {
        var money = new Money(50_000_000.00m, Usd);

        var parts = money.Allocate([int.MaxValue, 1]);

        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Allocating_5_billion_VND_a_zero_exponent_currency_by_several_ratios_sums_to_exactly_the_original_amount()
    {
        var money = new Money(5_000_000_000m, Vnd);

        var parts = money.Allocate([7, 13, 29, 51, 1_000]);

        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Allocating_100_billion_USD_by_1_million_and_1_sums_to_exactly_the_original_amount()
    {
        var money = new Money(100_000_000_000.00m, Usd);

        var parts = money.Allocate([1_000_000, 1]);

        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Allocating_an_amount_whose_minor_units_dont_fit_a_64_bit_integer_is_an_InvalidAllocationException()
    {
        // Far beyond long.MaxValue / 100 (~92.23 quadrillion), the documented range
        // limit for a 2-decimal currency: this must fail closed and explicitly, not with
        // an incidental OverflowException from decimal.ToInt64 deep inside Allocate.
        var huge = new Money(100_000_000_000_000_000_000m, Usd);

        var act = () => huge.Allocate(2);

        // Throw<T>() itself fails if any other exception type (e.g. the incidental
        // OverflowException decimal.ToInt64 would otherwise throw) escapes instead.
        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void Allocating_into_exactly_MaxAllocationParts_parts_succeeds()
    {
        var money = new Money(100.00m, Usd);

        var parts = money.Allocate(Money.MaxAllocationParts);

        parts.Should().HaveCount(Money.MaxAllocationParts);
        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Allocating_into_more_than_MaxAllocationParts_parts_is_rejected()
    {
        var money = new Money(100.00m, Usd);

        var act = () => money.Allocate(Money.MaxAllocationParts + 1);

        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void Allocating_by_exactly_MaxAllocationParts_ratios_succeeds()
    {
        var money = new Money(100.00m, Usd);
        var ratios = Enumerable.Repeat(1, Money.MaxAllocationParts).ToArray();

        var parts = money.Allocate(ratios);

        parts.Should().HaveCount(Money.MaxAllocationParts);
        parts.Aggregate((a, b) => a + b).Should().Be(money);
    }

    [Fact]
    public void Allocating_by_more_than_MaxAllocationParts_ratios_is_rejected()
    {
        var money = new Money(100.00m, Usd);
        var ratios = Enumerable.Repeat(1, Money.MaxAllocationParts + 1).ToArray();

        var act = () => money.Allocate(ratios);

        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void An_oversized_lazy_ratio_list_is_rejected_by_Count_alone_without_being_enumerated()
    {
        // Security review 0.04, N2: Count must be checked (both zero and over-limit)
        // before any Any()/enumeration scan, so a hostile or merely huge lazy
        // IReadOnlyList<int> is rejected on its Count alone. This list throws the moment
        // anything tries to read an element or enumerate it, so the test itself fails
        // (rather than the assertion below) if Allocate ever enumerates first.
        var money = new Money(100.00m, Usd);
        var ratios = new ThrowsIfEnumeratedRatioList(Money.MaxAllocationParts + 1);

        var act = () => money.Allocate(ratios);

        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void A_ratio_list_reporting_a_negative_Count_is_rejected_as_an_InvalidAllocationException()
    {
        // Security review 0.04, N7: a negative Count must fail explicitly, not with an
        // incidental OverflowException from allocating an array of negative length.
        var money = new Money(100.00m, Usd);
        var ratios = new ThrowsIfEnumeratedRatioList(-1);

        var act = () => money.Allocate(ratios);

        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void A_ratio_list_whose_indexer_disagrees_with_its_enumerator_cannot_slip_a_non_positive_ratio_past_validation()
    {
        // Security review 0.04, N7: validation and arithmetic must read the same values.
        // This list enumerates as [1, 1, 1] but its indexer returns 0 for every element.
        var money = new Money(100.00m, Usd);
        var ratios = new InconsistentRatioList();

        var act = () => money.Allocate(ratios);

        act.Should().Throw<InvalidAllocationException>();
    }

    [Fact]
    public void Allocating_minus_long_MaxValue_JPY_mirrors_the_positive_allocation()
    {
        // Security review 0.04, N8: the documented range is symmetric, ±long.MaxValue
        // minor units.
        var jpy = Currency.FromCode("JPY");
        var positive = new Money(long.MaxValue, jpy);
        var negative = new Money(-(decimal)long.MaxValue, jpy);

        var positiveParts = positive.Allocate([int.MaxValue, 1]);
        var negativeParts = negative.Allocate([int.MaxValue, 1]);

        negativeParts.Should().Equal(positiveParts.Select(p => -p));
        negativeParts.Aggregate((a, b) => a + b).Should().Be(negative);
    }

    [Fact]
    public void Allocating_long_MinValue_JPY_is_outside_the_symmetric_range_and_is_an_InvalidAllocationException()
    {
        // Security review 0.04, N8: long.MinValue minor units has no positive mirror in
        // 64 bits, so it is just outside the documented ±long.MaxValue range.
        var money = new Money(long.MinValue, Currency.FromCode("JPY"));

        var act = () => money.Allocate(2);

        act.Should().Throw<InvalidAllocationException>();
    }

    /// <summary>
    /// An <see cref="IReadOnlyList{T}"/> whose enumerator yields positive ratios while its
    /// indexer yields zero — a caller-supplied list that is inconsistent between reads.
    /// </summary>
    private sealed class InconsistentRatioList : IReadOnlyList<int>
    {
        public int Count => 3;

        public int this[int index] => 0;

        public IEnumerator<int> GetEnumerator() => Enumerable.Repeat(1, Count).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// An <see cref="IReadOnlyList{T}"/> that reports a fixed <see cref="Count"/> but
    /// throws on any indexer access or enumeration — standing in for a lazy/expensive or
    /// hostile caller-supplied list whose contents must never be scanned once its
    /// declared length alone is enough to reject it.
    /// </summary>
    private sealed class ThrowsIfEnumeratedRatioList(int count) : IReadOnlyList<int>
    {
        public int Count { get; } = count;

        public int this[int index] => throw new InvalidOperationException(
            "Indexer accessed: Allocate must reject an oversized ratio list by Count alone.");

        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException(
            "Enumerated: Allocate must reject an oversized ratio list by Count alone.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
