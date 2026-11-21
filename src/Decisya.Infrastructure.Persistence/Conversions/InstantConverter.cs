using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace Decisya.Infrastructure.Persistence.Conversions;

/// <summary>
/// Maps NodaTime's <see cref="Instant"/> to Postgres <c>timestamptz</c> (issue #341, G2 D10), so a
/// module needs no <c>Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime</c> package for it. Register it
/// in the module context's <c>ConfigureConventions</c>, after <c>base</c>.
/// </summary>
public sealed class InstantConverter : ValueConverter<Instant, DateTimeOffset>
{
    public InstantConverter()
        : base(instant => instant.ToDateTimeOffset(), dateTimeOffset => Instant.FromDateTimeOffset(dateTimeOffset))
    {
    }
}
