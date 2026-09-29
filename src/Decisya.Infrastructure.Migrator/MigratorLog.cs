using Microsoft.Extensions.Logging;

namespace Decisya.Infrastructure.Migrator;

/// <summary>
/// The compiled log messages <c>Program</c> writes (CA1848). Never a connection string, a
/// password or a SQL statement — only the exception's own message, which is already scoped to
/// naming a missing or invalid configuration <em>key</em> (G3, G4-21-05).
/// </summary>
internal static partial class MigratorLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Error, Message = "{Reason}")]
    internal static partial void ConfigurationError(ILogger logger, string reason);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Tenancy module migration and role provisioning completed.")]
    internal static partial void MigrationCompleted(ILogger logger);
}
