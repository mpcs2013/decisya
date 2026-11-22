using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Ledger.Domain;

/// <summary>
/// One row per space: the lock that serializes account creation within the space (issue #341, G2 "The
/// two-account limit"). <see cref="LockVersion"/> is incremented only by the lock statement in
/// <c>CreateAccountHandler</c>; the row holds no money.
/// </summary>
internal sealed class AccountBook : ITenantScoped
{
    private AccountBook()
    {
        // EF Core materialization constructor.
    }

    private AccountBook(TenantId tenantId, Instant now)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public long LockVersion { get; private set; }

    public Instant CreatedAt { get; private set; }

    public static AccountBook Open(TenantId tenantId, Instant now) => new(tenantId, now);
}
