using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace Decisya.Infrastructure.Persistence.Conversions;

/// <summary>
/// Maps NodaTime's <see cref="LocalDate"/> to Postgres <c>date</c> through <see cref="DateOnly"/> (issue
/// #341, G2 D10), so a module needs no <c>Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime</c> package.
/// </summary>
public sealed class LocalDateConverter : ValueConverter<LocalDate, DateOnly>
{
    public LocalDateConverter()
        : base(date => date.ToDateOnly(), dateOnly => LocalDate.FromDateOnly(dateOnly))
    {
    }
}
