using Microsoft.EntityFrameworkCore;

namespace Decisya.Api.Errors;

/// <summary>The compiled log message <see cref="ConcurrencyConflictExceptionHandler"/> writes (CA1848). The exception object itself carries the full detail; request-scoped enrichment supplies the trace id.</summary>
internal static partial class ConcurrencyConflictExceptionHandlerLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning, Message = "A database concurrency conflict was reported to the caller as a generic 404.")]
    internal static partial void ConcurrencyConflict(ILogger logger, DbUpdateConcurrencyException exception);
}
