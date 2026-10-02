using System.Data.Common;

namespace Decisya.Modules.Audit.Contracts;

/// <summary>
/// The Audit module's only public operation (issue #24, G2; ADR-0013): append one
/// <c>succeeded</c> audit record inside the caller's own, open database transaction, so the
/// record commits if and only if the audited command commits. There is no read, update or
/// delete operation, and the audit table is append-only at the database role.
/// Registered scoped.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Inserts one record on <paramref name="transaction"/>'s connection, enlisted in that
    /// transaction. It never opens a connection, never commits and never rolls back: the caller
    /// commits after this returns, or disposes the transaction to roll both changes back.
    /// The connection's database role needs <c>INSERT</c> on <c>audit.audit_records</c> and
    /// nothing else there (ADR-0013).
    /// </summary>
    /// <exception cref="ArgumentException">The entry is malformed: uninitialized tenant, undefined action, or a feature key that is missing, present when it must not be, or of the wrong shape.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="transaction"/> has completed (no connection); the ambient caller has no
    /// validated user id; or the ambient tenant resolution is <c>Invalid</c> or names a tenant
    /// other than the entry's. Messages are fixed and never contain entry values.
    /// </exception>
    Task AppendAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken = default);
}
