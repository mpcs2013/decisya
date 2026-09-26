namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed implementation of <see cref="ILogEnrichmentContext"/>.
/// The <see cref="AsyncLocal{T}"/> is an instance field, so two hosts built in the same
/// process (as the tests do) never see each other's values.
/// </summary>
internal sealed class LogEnrichmentContext(UserIdHasher hasher) : ILogEnrichmentContext
{
    private readonly AsyncLocal<Values?> _current = new();

    public string? TenantId => _current.Value?.TenantId;

    public string? UserIdHash => _current.Value?.UserIdHash;

    public IDisposable Begin(string? tenantId, string? userId)
    {
        var previous = _current.Value;
        _current.Value = new Values(tenantId, hasher.Hash(userId));

        return new Handle(_current, previous);
    }

    private sealed record Values(string? TenantId, string? UserIdHash);

    private sealed class Handle(AsyncLocal<Values?> slot, Values? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            slot.Value = previous;
        }
    }
}
