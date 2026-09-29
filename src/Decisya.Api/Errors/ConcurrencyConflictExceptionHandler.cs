using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Api.Errors;

/// <summary>
/// Reports a <see cref="DbUpdateConcurrencyException"/> as a generic 404, never with any
/// database value from either side of the conflict (issue #21, Story 5; carries in #22 B-3;
/// G2, G4-21-04; NFR-33). Registered with <c>AddExceptionHandler&lt;&gt;()</c> before
/// <c>AddProblemDetails()</c>; <c>UseExceptionHandler</c> already runs first in the pipeline.
/// </summary>
/// <remarks>
/// Also matches when the exception arrives wrapped as an <see cref="Exception.InnerException"/>
/// (for example, inside an <see cref="AggregateException"/> from a parallel save). Never calls
/// <c>GetDatabaseValues</c>, <c>Reload</c> or reads <c>Entries</c> — <c>CrossTenantQueryRule</c>
/// already bans both outside an <c>[AllowCrossTenant]</c> type, and this handler has no need
/// for either: the 404 body carries only <c>status</c>, <c>title</c>, <c>type</c> and
/// <c>traceId</c>. The full exception, with a trace id, reaches only the structured log.
/// </remarks>
public sealed class ConcurrencyConflictExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ConcurrencyConflictExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var concurrencyException = exception as DbUpdateConcurrencyException
            ?? exception.InnerException as DbUpdateConcurrencyException;

        if (concurrencyException is null)
        {
            return false;
        }

        ConcurrencyConflictExceptionHandlerLog.ConcurrencyConflict(logger, concurrencyException);

        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;

        var problemDetails = new ProblemDetails { Status = StatusCodes.Status404NotFound };
        // traceId only: never an entity name, column name or row value from either side of the
        // conflict (G2, NFR-33). Added here directly so the body carries it regardless of
        // whether the host also configures CustomizeProblemDetails globally.
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = null, // never the exception: no entity, column or row value reaches the client.
            ProblemDetails = problemDetails,
        }).ConfigureAwait(false);
    }
}
