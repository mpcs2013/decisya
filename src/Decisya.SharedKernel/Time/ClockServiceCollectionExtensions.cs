using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Decisya.SharedKernel.Time;

/// <summary>
/// Composition-root registration for <see cref="IClock"/>. Every module depends on
/// <see cref="IClock"/> via dependency injection; nothing outside
/// <see cref="Decisya.SharedKernel"/> (and tests, which use
/// <c>NodaTime.Testing.FakeClock</c>) should reference <see cref="SystemClock"/>
/// directly. See docs/requirements/phase-0/shared-kernel.md, Story 4.
/// </summary>
public static class ClockServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IClock"/> as a singleton backed by NodaTime's real-world
    /// system clock. Call this once from each service's composition root (e.g.
    /// alongside <c>AddServiceDefaults</c>).
    /// </summary>
    public static IServiceCollection AddSystemClock(this IServiceCollection services) =>
        services.AddSingleton<IClock>(SystemClock.Instance);
}
