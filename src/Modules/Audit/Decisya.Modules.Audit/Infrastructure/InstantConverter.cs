using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace Decisya.Modules.Audit.Infrastructure;

/// <summary>
/// Maps NodaTime's <see cref="Instant"/> to Postgres <c>timestamptz</c> (issue #24, G2). The third
/// copy of the Tenancy and Entitlements converter; moving all three into
/// <c>Decisya.Infrastructure.Persistence</c> is tracked in backlog #83.
/// </summary>
internal sealed class InstantConverter : ValueConverter<Instant, DateTimeOffset>
{
    public InstantConverter()
        : base(instant => instant.ToDateTimeOffset(), dateTimeOffset => Instant.FromDateTimeOffset(dateTimeOffset))
    {
    }
}
