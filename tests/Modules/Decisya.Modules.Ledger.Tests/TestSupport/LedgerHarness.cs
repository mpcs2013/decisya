using System.Security.Cryptography;
using Decisya.Infrastructure.Migrator;
using Decisya.Modules.Audit;
using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Contracts;
using Decisya.Modules.Ledger.Domain;
using Decisya.Modules.Ledger.Infrastructure;
using Decisya.SharedKernel;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using NpgsqlTypes;

namespace Decisya.Modules.Ledger.Tests.TestSupport;

/// <summary>
/// The real Ledger module, wired the way the API wires it (<c>AddAuditModule</c>, then <c>AddLedgerModule</c>),
/// over a fresh Postgres 18 database migrated by the real <c>MigrationRunner.RunAsync</c> and connected as the
/// real <c>decisya_ledger</c> role (G2 "Parallel test"). Every call runs in its own DI scope with its own
/// <see cref="TestCurrentTenant"/> and <see cref="TestCurrentCaller"/>, so each one is what a separate request
/// would be. Direct reads and seeding go through the owner connection, never the handlers' role.
/// </summary>
internal sealed class LedgerHarness : IAsyncDisposable
{
    /// <summary>The fixed start instant of the scenarios.</summary>
    internal static readonly Instant Start = Instant.FromUtc(2026, 11, 21, 9, 0);

    internal static readonly LocalDate OpeningDate = new(2026, 11, 1);

    // Postgres roles are cluster-wide: every harness in this assembly's one container serialises on this
    // gate (CREATE ROLE / ALTER ROLE race otherwise), and all of them use the same three role passwords, so a
    // re-run never changes what an open login accepts.
    private static readonly SemaphoreSlim MigratorGate = new(1, 1);
    private static readonly string TenancyRolePassword = NewPassword();
    private static readonly string EntitlementsRolePassword = NewPassword();
    private static readonly string LedgerRolePassword = NewPassword();

    private readonly ServiceProvider _provider;

    private LedgerHarness(string ownerConnectionString, string serviceConnectionString, Action<IServiceCollection>? configure)
    {
        OwnerConnectionString = ownerConnectionString;
        ServiceConnectionString = serviceConnectionString;
        Clock = new FakeClock(Start);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IClock>(Clock);
        services.AddScoped<TestCurrentTenant>();
        services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<TestCurrentTenant>());
        services.AddScoped(_ => new TestCurrentCaller { Id = CallerUserId });
        services.AddScoped<ICurrentCaller>(sp => sp.GetRequiredService<TestCurrentCaller>());
        services.AddAuditModule();
        services.AddLedgerModule(serviceConnectionString);
        configure?.Invoke(services);

        // ValidateOnBuild stays off: the Ledger.Owner handler needs Tenancy's ICallerTenantRole, which the
        // API registers and these module tests do not (the handlers under test never use it).
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public FakeClock Clock { get; }

    /// <summary>The database owner's connection string (superuser of the throwaway container). Never logged or put in a message.</summary>
    internal string OwnerConnectionString { get; }

    /// <summary>The connection string of <c>decisya_ledger</c>, the handlers' role. Never logged or put in a message.</summary>
    internal string ServiceConnectionString { get; }

    /// <summary>The user id every scope created from now on carries as <see cref="ICurrentCaller.UserId"/>.</summary>
    public string? CallerUserId { get; set; } = TestCurrentCaller.DefaultUserId;

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    /// <summary>
    /// A fresh database migrated by the real <c>MigrationRunner.RunAsync</c>; the handlers connect as
    /// <c>decisya_ledger</c> (SELECT, INSERT, UPDATE on <c>ledger</c>; INSERT on <c>audit.audit_records</c>).
    /// </summary>
    public static async Task<LedgerHarness> CreateAsync(
        PostgresFixture pg, CancellationToken cancellationToken, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(pg);

        var owner = await pg.CreateEmptyDatabaseAsync(cancellationToken);

        await MigratorGate.WaitAsync(cancellationToken);
        try
        {
            await MigrationRunner.RunAsync(owner, TenancyRolePassword, EntitlementsRolePassword, LedgerRolePassword, cancellationToken);
        }
        finally
        {
            MigratorGate.Release();
        }

        var service = new NpgsqlConnectionStringBuilder(owner)
        {
            Username = LedgerModule.DatabaseRole,
            Password = LedgerRolePassword,
        }.ConnectionString;

        return new LedgerHarness(owner, service, configure);
    }

    /// <summary>A scope that is one request: a fresh tenant resolution, caller and DbContext.</summary>
    public IServiceScope Scope(TenantResolution ambient)
    {
        var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestCurrentTenant>().Resolution = ambient;
        return scope;
    }

    public IServiceScope Scope(TenantId tenant) => Scope(TenantResolution.For(tenant));

    /// <summary>Builds a validated create command (the endpoint's job in production).</summary>
    public static CreateAccount NewCreate(string name = "Everyday", string currency = "EUR", long? openingMinor = null)
    {
        var code = Currency.FromCode(currency);
        Money? opening = openingMinor is { } minor ? Money.FromMinorUnits(minor, code) : null;
        return new CreateAccount(NewName(name), code, opening, opening is null ? null : OpeningDate);
    }

    public static AccountName NewName(string name) =>
        AccountName.TryCreate(name, out var accountName)
            ? accountName
            : throw new InvalidOperationException("The test name is not a valid account name.");

    public async Task<AccountCommandResult> CreateAccountAsync(
        TenantId tenant, CancellationToken cancellationToken, string name = "Everyday", string currency = "EUR", long? openingMinor = null)
    {
        using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<CreateAccountHandler>()
            .HandleAsync(NewCreate(name, currency, openingMinor), cancellationToken);
    }

    public async Task<AccountCommandResult> CreateAccountAsync(
        TenantResolution ambient, CancellationToken cancellationToken)
    {
        using var scope = Scope(ambient);
        return await scope.ServiceProvider.GetRequiredService<CreateAccountHandler>()
            .HandleAsync(NewCreate(), cancellationToken);
    }

    public async Task<AccountCommandResult> RenameAccountAsync(
        TenantResolution ambient, Guid accountId, string name, CancellationToken cancellationToken)
    {
        using var scope = Scope(ambient);
        return await scope.ServiceProvider.GetRequiredService<RenameAccountHandler>()
            .HandleAsync(new RenameAccount(accountId, NewName(name)), cancellationToken);
    }

    public Task<AccountCommandResult> RenameAccountAsync(
        TenantId tenant, Guid accountId, string name, CancellationToken cancellationToken) =>
        RenameAccountAsync(TenantResolution.For(tenant), accountId, name, cancellationToken);

    public async Task<AccountCommandResult> ChangeCurrencyAsync(
        TenantResolution ambient, Guid accountId, string currency, long? openingMinor, CancellationToken cancellationToken)
    {
        var code = Currency.FromCode(currency);
        Money? opening = openingMinor is { } minor ? Money.FromMinorUnits(minor, code) : null;

        using var scope = Scope(ambient);
        return await scope.ServiceProvider.GetRequiredService<ChangeAccountCurrencyHandler>()
            .HandleAsync(
                new ChangeAccountCurrency(accountId, code, opening, opening is null ? null : OpeningDate),
                cancellationToken);
    }

    public Task<AccountCommandResult> ChangeCurrencyAsync(
        TenantId tenant, Guid accountId, string currency, long? openingMinor, CancellationToken cancellationToken) =>
        ChangeCurrencyAsync(TenantResolution.For(tenant), accountId, currency, openingMinor, cancellationToken);

    public async Task<AccountListResponse> ListAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<AccountQueries>().ListAsync(cancellationToken);
    }

    public async Task<AccountDto?> GetAccountAsync(TenantId tenant, Guid accountId, CancellationToken cancellationToken)
    {
        using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<AccountQueries>().GetAsync(accountId, cancellationToken);
    }

    /// <summary>Options for a context that reads and seeds directly: always the owner connection, never the handlers' role.</summary>
    public DbContextOptions<LedgerDbContext> NewOptions()
    {
        var builder = new DbContextOptionsBuilder<LedgerDbContext>();
        LedgerDbContextOptions.Configure(builder, OwnerConnectionString);
        return builder.Options;
    }

    /// <summary>A context for direct reads and seeding, under a fixed ambient tenant. Never the handlers' own context.</summary>
    public LedgerDbContext Context(TenantId tenant) =>
        new(NewOptions(), new TestCurrentTenant { Resolution = TenantResolution.For(tenant) });

    public async Task<List<Account>> AccountsOfAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        await using var db = Context(tenant);
        return await db.Accounts.AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(cancellationToken);
    }

    public async Task<List<AccountBook>> BooksOfAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        await using var db = Context(tenant);
        return await db.AccountBooks.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <summary>Every ledger row of the whole database, any tenant, as the owner (raw), for "nothing was written anywhere" checks.</summary>
    public async Task<long> TotalLedgerRowsAsync(CancellationToken cancellationToken) =>
        await CountAsOwnerAsync("SELECT (SELECT count(*) FROM ledger.accounts) + (SELECT count(*) FROM ledger.account_books)", cancellationToken);

    /// <summary>Every audit record of one tenant, read as the owner (the application role cannot SELECT the table).</summary>
    public async Task<List<AuditRow>> AuditRowsOfAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        var rows = new List<AuditRow>();
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, tenant_id, occurred_at, actor_user_id, action, outcome, feature_key, trace_id, subject_id " +
            "FROM audit.audit_records WHERE tenant_id = @tenant ORDER BY id",
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8)));
        }

        return rows;
    }

    /// <summary>Every audit record of the database as one text per row (<c>t::text</c>), any tenant: a scan target for "no name, amount or currency amount".</summary>
    public Task<List<string>> AuditRowTextsAsync(CancellationToken cancellationToken) =>
        QueryAsOwnerAsync("SELECT t::text FROM audit.audit_records t", cancellationToken);

    /// <summary>Runs one statement as the owner (fault injection such as a REVOKE).</summary>
    public async Task<int> ExecuteAsOwnerAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);
#pragma warning disable CA2100 // every call site passes a fixed literal or a string built from module constants.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The first column of every row of one owner-run query, as text (schema introspection).</summary>
    public async Task<List<string>> QueryAsOwnerAsync(string sql, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);
#pragma warning disable CA2100 // every call site passes a fixed literal or a string built from module constants.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return values;
    }

    public async Task<long> CountAsOwnerAsync(string sql, CancellationToken cancellationToken) =>
        long.Parse((await QueryAsOwnerAsync(sql, cancellationToken)).Single(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Inserts one account row with raw SQL on a caller-owned connection and transaction (the lock-wait
    /// scenario: a second writer commits an account while the first create waits for the space's lock).
    /// </summary>
    public static async Task InsertRawAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenant, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO ledger.accounts (id, tenant_id, name, currency, opening_balance_minor, opening_date, created_at) " +
            "VALUES (@id, @tenant, 'Held', 'EUR', 0, NULL, now())",
            connection,
            transaction);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("tenant", tenant.Value);
        (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// Inserts one raw audit row through <paramref name="connectionString"/>, with any combination of action, feature
    /// key and subject: the layer that holds when the writer is bypassed (G3 M341-03 (b)). No RETURNING, so it
    /// needs INSERT only, which is all <c>decisya_ledger</c> holds.
    /// </summary>
    public static async Task<int> InsertRawAuditRowAsync(
        string connectionString, string action, string? featureKey, Guid? subjectId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO audit.audit_records (id, tenant_id, occurred_at, actor_user_id, action, outcome, feature_key, trace_id, subject_id) " +
            "VALUES (@id, @tenant, now(), 'raw-test-actor', @action, 'succeeded', @feature, NULL, @subject)",
            connection);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("tenant", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("action", action);
        insert.Parameters.Add(new NpgsqlParameter("feature", NpgsqlDbType.Text) { Value = (object?)featureKey ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("subject", NpgsqlDbType.Uuid) { Value = (object?)subjectId ?? DBNull.Value });
        return await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Opens <paramref name="count"/> connections as <c>decisya_ledger</c> at once and returns them to the pool, so a
    /// parallel round starts with warm connections and the creates really overlap.
    /// </summary>
    public async Task WarmPoolAsync(int count, CancellationToken cancellationToken)
    {
        var connections = new List<NpgsqlConnection>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var connection = new NpgsqlConnection(ServiceConnectionString);
                connections.Add(connection);
                await connection.OpenAsync(cancellationToken);
            }
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Disposes the provider and closes this database's pooled connections (both users): every harness has its own
    /// database, so its pools would otherwise idle until the shared container hits max_connections (53300).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        foreach (var connectionString in new[] { OwnerConnectionString, ServiceConnectionString })
        {
            await using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
        }
    }
}

/// <summary>One row of <c>audit.audit_records</c>, as the owner reads it.</summary>
internal sealed record AuditRow(
    Guid Id,
    Guid TenantId,
    Instant OccurredAt,
    string ActorUserId,
    string Action,
    string Outcome,
    string? FeatureKey,
    string? TraceId,
    Guid? SubjectId);
