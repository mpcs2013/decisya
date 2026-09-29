namespace Decisya.Api.Authentication;

/// <summary>
/// The compiled log messages <see cref="CallerContextMiddleware"/> writes (CA1848). No message
/// carries the caller's raw <c>sub</c> or the raw <c>tenant_id</c> claim value (G2, G3): the
/// request's own enrichment (once it resolves) supplies the hashed user id and the tenant id
/// on every later log record instead.
/// </summary>
internal static partial class CallerContextMiddlewareLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Caller context rejected: the authenticated principal's claims cannot be trusted as a single identity.")]
    internal static partial void InvalidIdentity(ILogger logger);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Caller context rejected: the tenant_id claim is malformed.")]
    internal static partial void InvalidTenantClaim(ILogger logger);
}
