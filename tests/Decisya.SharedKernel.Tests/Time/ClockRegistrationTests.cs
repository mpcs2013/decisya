using Decisya.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Decisya.SharedKernel.Tests.Time;

/// <summary>
/// Story 4, Scenario 1 — the production composition root resolves a system-backed
/// clock. Builds a real <see cref="IServiceCollection"/> exactly as a service's Program
/// would, and proves IClock resolves to a real-world instant.
/// </summary>
public class ClockRegistrationTests
{
    [Fact]
    public void Production_composition_root_resolves_a_system_backed_clock()
    {
        var services = new ServiceCollection();
        services.AddSystemClock();

        using var provider = services.BuildServiceProvider();
        var clock = provider.GetRequiredService<IClock>();

        var resolvedInstant = clock.GetCurrentInstant();
        var realInstant = SystemClock.Instance.GetCurrentInstant();
        var difference = realInstant >= resolvedInstant
            ? realInstant - resolvedInstant
            : resolvedInstant - realInstant;

        // Both come from the real-world clock: they must be within a few seconds of
        // each other (not a fixed/fake instant).
        difference.Should().BeLessThan(Duration.FromSeconds(5));
    }

    [Fact]
    public void AddSystemClock_registers_IClock_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddSystemClock();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IClock>();
        var second = provider.GetRequiredService<IClock>();

        first.Should().BeSameAs(second);
    }
}
