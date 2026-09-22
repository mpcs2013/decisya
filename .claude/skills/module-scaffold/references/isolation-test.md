# Two-tenant isolation test template

```csharp
[Trait("Category", "Integration")]
public sealed class <Name>IsolationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Tenant_A_cannot_read_rows_of_tenant_B()
    {
        var a = TenantId.New(); var b = TenantId.New();
        await using (var ctx = pg.CreateContext<<Name>DbContext>(b))
        {
            ctx.Add(<entity for tenant b>);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = pg.CreateContext<<Name>DbContext>(a))
        {
            (await ctx.<Set>.ToListAsync()).Should().BeEmpty();
        }
    }
}
```
