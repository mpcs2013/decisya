using NodaTime;
using NodaTime.Testing;

namespace Decisya.SharedKernel.Tests.Time;

/// <summary>
/// Story 4, Scenario 2 — tests can pin and advance time deterministically via
/// <see cref="FakeClock"/> (the standard NodaTime.Testing companion to
/// <see cref="IClock"/>), instead of ever touching the real wall clock.
/// </summary>
public class FakeClockTests
{
    [Fact]
    public void A_fixed_fake_clock_reports_exactly_the_pinned_instant()
    {
        var pinned = Instant.FromUtc(2026, 1, 15, 0, 0, 0);
        var clock = new FakeClock(pinned);

        clock.GetCurrentInstant().Should().Be(pinned);
    }

    [Fact]
    public void Advancing_the_fake_clock_by_one_day_moves_it_forward_exactly_one_day()
    {
        var pinned = Instant.FromUtc(2026, 1, 15, 0, 0, 0);
        var clock = new FakeClock(pinned);

        clock.AdvanceDays(1);

        clock.GetCurrentInstant().Should().Be(Instant.FromUtc(2026, 1, 16, 0, 0, 0));
    }

    [Fact]
    public void A_component_depending_on_IClock_observes_the_fake_clocks_instant()
    {
        var pinned = Instant.FromUtc(2026, 1, 15, 0, 0, 0);
        IClock clock = new FakeClock(pinned);

        Reads(clock).Should().Be(pinned);

        ((FakeClock)clock).AdvanceDays(1);

        Reads(clock).Should().Be(Instant.FromUtc(2026, 1, 16, 0, 0, 0));

        static Instant Reads(IClock c) => c.GetCurrentInstant();
    }
}
