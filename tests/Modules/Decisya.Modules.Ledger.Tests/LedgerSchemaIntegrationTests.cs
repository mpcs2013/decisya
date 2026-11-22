using Decisya.TestInfrastructure;
using static Decisya.Modules.Ledger.Tests.TestSupport.LedgerHarness;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G1 Story 8 (NFR-82, NFR-84), "The schema follows the rules" and "No IBAN or card field exists", against the
/// database the real migrator produced: every ledger table is in the <c>ledger</c> schema, has
/// <c>tenant_id uuid not null</c> and an index whose first column is <c>tenant_id</c>; the money columns are
/// exactly <c>opening_balance_minor bigint</c> and <c>currency char(3)</c>; the named checks and indexes exist;
/// and no column is an IBAN, card or account number.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LedgerSchemaIntegrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task The_migration_creates_exactly_the_two_ledger_tables_and_the_history_table_in_the_ledger_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.QueryAsOwnerAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'ledger' ORDER BY table_name", ct))
            .Should().Equal("__EFMigrationsHistory", "account_books", "accounts");

        // No ledger table outside the schema.
        (await h.CountAsOwnerAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('accounts', 'account_books') AND table_schema <> 'ledger'", ct))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("account_books")]
    public async Task Every_ledger_table_has_tenant_id_uuid_not_null_and_an_index_that_starts_with_it(string table)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.QueryAsOwnerAsync(
            "SELECT data_type || '/' || is_nullable FROM information_schema.columns " +
            $"WHERE table_schema = 'ledger' AND table_name = '{table}' AND column_name = 'tenant_id'", ct))
            .Should().Equal("uuid/NO");

        (await h.CountAsOwnerAsync(
            "SELECT count(*) FROM pg_index i " +
            "JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0] " +
            $"WHERE n.nspname = 'ledger' AND t.relname = '{table}' AND a.attname = 'tenant_id'", ct))
            .Should().BeGreaterThanOrEqualTo(1);

        // Every index of the table starts with tenant_id (the primary key aside, which is the row id).
        (await h.CountAsOwnerAsync(
            "SELECT count(*) FROM pg_index i " +
            "JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0] " +
            $"WHERE n.nspname = 'ledger' AND t.relname = '{table}' AND NOT i.indisprimary AND a.attname <> 'tenant_id'", ct))
            .Should().Be(0, "no secondary index leads with another column");
    }

    [Fact]
    public async Task The_money_columns_are_exactly_a_bigint_amount_in_minor_units_and_a_char_3_currency()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.QueryAsOwnerAsync(
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_schema = 'ledger' AND table_name = 'accounts' AND column_name = 'opening_balance_minor'", ct))
            .Should().Equal("bigint");

        (await h.QueryAsOwnerAsync(
            "SELECT data_type || '/' || character_maximum_length FROM information_schema.columns " +
            "WHERE table_schema = 'ledger' AND table_name = 'accounts' AND column_name = 'currency'", ct))
            .Should().Equal("character/3");

        (await h.QueryAsOwnerAsync(
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_schema = 'ledger' AND table_name = 'accounts' AND column_name = 'opening_date'", ct))
            .Should().Equal("date");

        // No floating-point, numeric or money column anywhere in the schema (NFR-82): amounts are bigint only.
        (await h.CountAsOwnerAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'ledger' " +
            "AND data_type IN ('double precision', 'real', 'numeric', 'money')", ct))
            .Should().Be(0);

        // The only columns that can carry money are the opening amount and the currency code.
        (await h.QueryAsOwnerAsync(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'ledger' " +
            "AND (column_name LIKE '%amount%' OR column_name LIKE '%balance%' OR column_name LIKE '%currency%') ORDER BY column_name", ct))
            .Should().Equal("currency", "opening_balance_minor");
    }

    [Fact]
    public async Task The_named_checks_and_indexes_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        foreach (var name in new[] { "ck_accounts_currency", "ck_accounts_name", "ck_accounts_opening" })
        {
            (await h.CountAsOwnerAsync(
                $"SELECT count(*) FROM pg_constraint WHERE conname = '{name}' AND conrelid = 'ledger.accounts'::regclass", ct))
                .Should().Be(1, name);
        }

        foreach (var name in new[] { "ix_accounts_tenant_created", "ux_account_books_tenant" })
        {
            (await h.CountAsOwnerAsync(
                $"SELECT count(*) FROM pg_indexes WHERE schemaname = 'ledger' AND indexname = '{name}'", ct))
                .Should().Be(1, name);
        }

        (await h.CountAsOwnerAsync(
            "SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relname = 'ux_account_books_tenant' AND i.indisunique", ct))
            .Should().Be(1, "one book per space");
    }

    [Fact]
    public async Task The_database_refuses_an_account_row_that_breaks_a_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var tenant = Guid.CreateVersion7();

        // check name, currency literal, opening amount, opening date literal
        var cases = new (string Check, string Currency, string Minor, string Date)[]
        {
            ("ck_accounts_currency", "'Eur'", "0", "NULL"),
            ("ck_accounts_currency", "'EU1'", "0", "NULL"),
            ("ck_accounts_opening", "'EUR'", "500", "NULL"),
        };

        foreach (var (check, currency, minor, date) in cases)
        {
            var sql =
                "INSERT INTO ledger.accounts (id, tenant_id, name, currency, opening_balance_minor, opening_date, created_at) " +
                $"VALUES (gen_random_uuid(), '{tenant}', 'Name', {currency}, {minor}, {date}, now())";
            var act = () => h.ExecuteAsOwnerAsync(sql, ct);

            var failure = await act.Should().ThrowAsync<Npgsql.PostgresException>(sql);
            failure.Which.ConstraintName.Should().Be(check, sql);
        }

        foreach (var name in new[] { string.Empty, new string('x', 81) })
        {
            var sql =
                "INSERT INTO ledger.accounts (id, tenant_id, name, currency, opening_balance_minor, opening_date, created_at) " +
                $"VALUES (gen_random_uuid(), '{tenant}', '{name}', 'EUR', 0, NULL, now())";
            var act = () => h.ExecuteAsOwnerAsync(sql, ct);

            // An 81-character name is refused by varchar(80) before the check; an empty one by ck_accounts_name.
            var failure = await act.Should().ThrowAsync<Npgsql.PostgresException>();
            failure.Which.SqlState.Should().BeOneOf(
                Npgsql.PostgresErrorCodes.CheckViolation, Npgsql.PostgresErrorCodes.StringDataRightTruncation);
        }

        (await h.TotalLedgerRowsAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task No_column_or_table_in_the_ledger_schema_is_an_IBAN_a_card_number_or_an_account_number()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.QueryAsOwnerAsync(
            "SELECT table_name || '.' || column_name FROM information_schema.columns WHERE table_schema = 'ledger' " +
            "AND (column_name ILIKE '%iban%' OR column_name ILIKE '%card%' OR column_name ILIKE '%account_number%' " +
            "OR column_name ILIKE '%acct%' OR column_name ILIKE '%sort_code%' OR column_name ILIKE '%routing%')", ct))
            .Should().BeEmpty();

        (await h.QueryAsOwnerAsync(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'ledger' AND table_name = 'accounts' ORDER BY column_name", ct))
            .Should().Equal("created_at", "currency", "id", "name", "opening_balance_minor", "opening_date", "tenant_id");
    }
}
