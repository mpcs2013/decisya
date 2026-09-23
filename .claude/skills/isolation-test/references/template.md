# Two-tenant isolation test template

Replace `<Name>`, `<Set>` and the entity construction. `TenantId`, `PostgresFixture` and `CreateContext<T>(TenantId)` come from #32 and #22; check with `python .claude/scripts/prereqs.py module-scaffold --phase tenancy`.

```csharp
[Trait("Category", "Integration")]
public sealed class <Name>IsolationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Tenant_A_cannot_read_rows_of_tenant_B()
    {
        var a = TenantId.New();
        var b = TenantId.New();

        await using (var ctx = pg.CreateContext<<Name>DbContext>(b))
        {
            ctx.Add(/* an entity owned by tenant b */);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = pg.CreateContext<<Name>DbContext>(a))
        {
            (await ctx.<Set>.ToListAsync()).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_rows_of_tenant_B()
    {
        // Same arrangement; load by id as tenant a and assert it is not found,
        // so an update or delete by id cannot reach tenant b's row (BOLA).
    }
}
```
