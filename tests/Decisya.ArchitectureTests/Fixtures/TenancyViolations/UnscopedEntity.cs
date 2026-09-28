namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// Story 7 red fixture: a type mapped into a <c>TenantDbContext</c> model that does not
/// implement <c>ITenantScoped</c>. Mapped by <see cref="ViolatingDbContext"/>.
/// </summary>
public sealed class UnscopedEntity
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Name { get; private set; } = string.Empty;
}
