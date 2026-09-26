namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Ambient tenant and user context for log records. Both sinks read it, so the
/// <c>tenant_id</c> and hashed <c>user_id</c> fields are identical on stdout and on OTLP.
/// </summary>
/// <remarks>
/// <para>
/// This replaces "logging scopes carry the tenant" as the source of <c>tenant_id</c>:
/// OTLP log scopes are read straight from the scope provider by the exporter, where the
/// masking core cannot reach them, so <c>IncludeScopes</c> is off for OTLP.
/// </para>
/// <para>
/// Nothing in <c>src/</c> calls <see cref="Begin"/> in issue #15, and nothing reads this
/// context for an authorization, tenant-resolution or branching decision. Logs are not
/// the audit trail. The first production callers are the API's auth middleware (#20) and
/// the Wolverine tenant middleware.
/// </para>
/// </remarks>
public interface ILogEnrichmentContext
{
    /// <summary>The current tenant id, or <see langword="null"/>.</summary>
    string? TenantId { get; }

    /// <summary>The keyed hash of the current user id, or <see langword="null"/>. Never a raw id.</summary>
    string? UserIdHash { get; }

    /// <summary>
    /// Sets the ambient tenant and user for the current asynchronous flow.
    /// <paramref name="userId"/> is hashed immediately and only the hash is stored.
    /// Disposing the returned handle restores the previous values.
    /// </summary>
    IDisposable Begin(string? tenantId, string? userId);
}
