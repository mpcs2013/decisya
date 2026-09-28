# Two-tenant isolation test template

Replace `<Name>`, `<Set>`, `<Entity>` and the entity construction. `TenantId` comes from #32. `PostgresFixture`, `CreateDatabaseAsync<T>()` and `CreateContext(TenantId)` come from #22 (`tests/Decisya.TestInfrastructure`); check with `python .claude/scripts/prereqs.py module-scaffold --phase tenancy`. The module test project registers the fixture once, as `tests/Decisya.Infrastructure.Persistence.Tests/AssemblyInfo.cs` does: `[assembly: Xunit.AssemblyFixture<Decisya.TestInfrastructure.PostgresFixture>]`. Each test gets its own database.

```csharp
[Trait("Category", "Integration")]
public sealed class <Name>IsolationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Tenant_A_cannot_read_rows_of_tenant_B()
    {
        var a = TenantId.New();
        var b = TenantId.New();
        var db = await pg.CreateDatabaseAsync<<Name>DbContext>();

        await using (var ctx = db.CreateContext(b))
        {
            ctx.Add(/* an entity owned by tenant b */);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.CreateContext(a))
        {
            (await ctx.<Set>.ToListAsync()).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_rows_of_tenant_B()
    {
        // Seed a row as tenant b, then as tenant a:
        // - load it by id: it is not found, so no handler can reach it (BOLA);
        // - attach a detached copy carrying b's id and call Update or Remove, then SaveChangesAsync:
        //   it throws DbUpdateConcurrencyException (TenantId is a concurrency token), and b's row is unchanged.
        // Never read the conflict's database values back (GetDatabaseValues or Reload): EF ignores the
        // tenant filter there (#22, ADR-0001); CrossTenantQueryRule bans them outside [AllowCrossTenant].
    }
}
```
