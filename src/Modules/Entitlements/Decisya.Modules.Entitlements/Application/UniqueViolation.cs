using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Decisya.Modules.Entitlements.Application;

internal static class UniqueViolation
{
    /// <summary>A Postgres 23505 on exactly the named constraint; any other database failure is not a handled race.</summary>
    public static bool Is(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
