using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace Decisya.Modules.Tenancy.Infrastructure;

/// <summary>
/// Maps NodaTime's <see cref="Instant"/> to Postgres <c>timestamptz</c> (issue #21, G2), so the
/// module needs no <c>Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime</c> package for its one
/// use of <see cref="Instant"/>. Registered in <see cref="TenancyDbContext.ConfigureConventions"/>
/// after <c>base</c>.
/// </summary>
internal sealed class InstantConverter : ValueConverter<Instant, DateTimeOffset>
{
    public InstantConverter()
        : base(instant => instant.ToDateTimeOffset(), dateTimeOffset => Instant.FromDateTimeOffset(dateTimeOffset))
    {
    }
}
