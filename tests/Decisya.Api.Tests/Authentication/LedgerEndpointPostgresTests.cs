using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Decisya.Modules.Ledger.Infrastructure;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #341, G5: the HTTP-level Gherkin scenarios of Stories 1 to 7 (AC-02-1 to AC-02-5) over the real
/// <c>Decisya.Api</c> host (Production environment, real JWT validation, real tenancy and entitlement gates, the
/// real endpoints, handlers, audit writer) and a real, fully migrated Postgres database including the ledger schema.
/// Every test has its own database and random tenant and user ids. The API connects as the database owner (the
/// least-privilege <c>decisya_ledger</c> role is proven by the Ledger and Migrator Integration tests). The antiforgery
/// refusal lives at the BFF and is proven in <c>Decisya.Bff.Tests.LedgerApiAntiforgeryTests</c>. Test names follow the
/// Gherkin scenario titles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LedgerEndpointPostgresTests(TenancyDatabaseFixture database) : IDisposable
{
    private const string Accounts = "/api/ledger/accounts";
    private const string EuroBody = """{"name":"Everyday","currency":"EUR"}""";

    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- Story 1: create an account ----

    [Fact]
    public async Task The_owner_creates_an_account_with_an_opening_balance()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        await stack.ProvisionAsync(space);

        using var response = await stack.SendAsync(
            HttpMethod.Post,
            Accounts,
            space.Owner,
            space.Tenant,
            """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":125000,"currency":"EUR"},"openingDate":"2026-12-01"}""");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = await JsonAsync(response);
        var root = body.RootElement;
        var id = root.GetProperty("id").GetGuid();
        response.Headers.Location!.OriginalString.Should().Be($"/api/ledger/accounts/{id:D}");
        root.GetProperty("name").GetString().Should().Be("Everyday");
        root.GetProperty("currency").GetString().Should().Be("EUR");
        root.GetProperty("openingBalance").GetProperty("amountMinor").GetInt64().Should().Be(125000);
        root.GetProperty("openingBalance").GetProperty("currency").GetString().Should().Be("EUR");
        root.GetProperty("openingDate").GetString().Should().Be("2026-12-01");
        root.GetProperty("balance").GetProperty("amountMinor").GetInt64().Should().Be(125000);
        root.GetProperty("currencyLocked").GetBoolean().Should().BeFalse();
        root.EnumerateObject().Select(p => p.Name).Should().NotContain(["tenantId", "tenant_id", "iban", "role"]);

        // The stored amount is a bigint of minor units and the stored currency is char(3) "EUR".
        (await stack.ScalarAsync(
            "SELECT opening_balance_minor::text || '/' || currency FROM ledger.accounts")).Should().Be("125000/EUR");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(1);
    }

    [Fact]
    public async Task The_opening_balance_is_optional()
    {
        await using var stack = await StartAsync();
        var space = Space.New();

        using var response = await stack.SendAsync(
            HttpMethod.Post, Accounts, space.Owner, space.Tenant, """{"name":"Savings","currency":"CHF"}""");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = await JsonAsync(response);
        body.RootElement.GetProperty("balance").GetProperty("amountMinor").GetInt64().Should().Be(0);
        body.RootElement.GetProperty("balance").GetProperty("currency").GetString().Should().Be("CHF");
    }

    [Theory]
    [InlineData("""{"name":"Everyday","currency":"JPY","openingBalance":{"amountMinor":5000,"currency":"JPY"},"openingDate":"2026-12-01"}""", "JPY", 5000L)]
    [InlineData("""{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":125000,"currency":"EUR"},"openingDate":"2026-12-01"}""", "EUR", 125000L)]
    [InlineData("""{"name":"Everyday","currency":"KWD","openingBalance":{"amountMinor":1500000,"currency":"KWD"},"openingDate":"2026-12-01"}""", "KWD", 1500000L)]
    public async Task Minor_units_follow_the_currencys_own_exponent(string request, string code, long minor)
    {
        await using var stack = await StartAsync();
        var space = Space.New();

        using var created = await stack.SendAsync(HttpMethod.Post, Accounts, space.Owner, space.Tenant, request);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var list = await stack.SendAsync(HttpMethod.Get, Accounts, space.Owner, space.Tenant);
        using var body = await JsonAsync(list);
        var balance = body.RootElement.GetProperty("accounts").EnumerateArray().Single().GetProperty("balance");
        balance.GetProperty("amountMinor").GetInt64().Should().Be(minor);
        balance.GetProperty("currency").GetString().Should().Be(code);
    }

    public static TheoryData<string, string> InvalidCreates => new()
    {
        { """{"name":"","currency":"EUR"}""", "ledger.name_invalid" },
        { $$"""{"name":"{{new string('x', 81)}}","currency":"EUR"}""", "ledger.name_invalid" },
        { """{"name":"Everyday","currency":"EURO"}""", "ledger.currency_invalid" },
        { """{"name":"Everyday","currency":"XXX"}""", "ledger.currency_invalid" },
        { """{"name":"Everyday","currency":"eur"}""", "ledger.currency_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":100,"currency":"EUR"}}""", "ledger.opening_pair_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingDate":"2026-12-01"}""", "ledger.opening_pair_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":9007199254740992,"currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.amount_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":9223372036854775808,"currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":12.5,"currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":"125000","currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","iban":"DE89370400440532013000"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","card_number":"4111111111111111"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","tenantId":"7c9e6679-7425-40de-944b-e07fc1f90ae7"}""", "ledger.body_invalid" },
        { """not json""", "ledger.body_invalid" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreates))]
    public async Task Invalid_input_is_refused_with_a_generic_validation_error_and_nothing_is_stored(string request, string expectedCode)
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        await stack.ProvisionAsync(space);

        using var response = await stack.SendAsync(HttpMethod.Post, Accounts, space.Owner, space.Tenant, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("code").GetString().Should().Be(expectedCode);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(["type", "title", "status", "traceId", "code"]);
        text.Should().NotContain("DE89370400440532013000").And.NotContain("4111111111111111", "a refused value is never echoed");
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    [Theory]
    [InlineData("POST", "", EuroBody, "text/plain")]
    [InlineData("PUT", "/name", """{"name":"Household"}""", "text/plain")]
    [InlineData("PUT", "/currency", """{"currency":"JPY","openingBalance":null,"openingDate":null}""", "text/plain")]
    [InlineData("POST", "", EuroBody, null)]
    [InlineData("PUT", "/name", """{"name":"Household"}""", null)]
    [InlineData("PUT", "/currency", """{"currency":"JPY","openingBalance":null,"openingDate":null}""", null)]
    public async Task A_request_that_is_not_JSON_is_refused_and_nothing_is_stored(string method, string suffix, string body, string? contentType)
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        var id = await CreateAsync(stack, space, "Everyday");
        var accountsBefore = await stack.CountAsync("SELECT count(*) FROM ledger.accounts");
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records");
        var httpMethod = new HttpMethod(method);
        var path = httpMethod == HttpMethod.Post ? Accounts : $"{Accounts}/{id}{suffix}";

        using var response = contentType is null
            ? await stack.SendWithoutContentTypeAsync(httpMethod, path, space.Owner, space.Tenant, body)
            : await stack.SendAsync(httpMethod, path, space.Owner, space.Tenant, body, contentType);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var problem = JsonDocument.Parse(text);
        problem.RootElement.GetProperty("code").GetString().Should().Be("ledger.body_invalid");
        problem.RootElement.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(["type", "title", "status", "traceId", "code"]);
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(accountsBefore);
        (await stack.ScalarAsync("SELECT name || '/' || currency FROM ledger.accounts")).Should().Be("Everyday/EUR");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore, "no audit record for the refused request");
    }

    [Fact]
    public async Task A_request_with_no_token_and_a_wrong_content_type_is_refused_as_unauthenticated_and_nothing_is_stored()
    {
        await using var stack = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, Accounts)
        {
            Content = new StringContent(EuroBody, Encoding.UTF8, "text/plain"),
        };

        using var anonymous = await stack.Client.SendAsync(request, TestContext.Current.CancellationToken);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "authentication comes before the content-type check");
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    [Fact]
    public async Task A_member_cannot_create_an_account()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        await stack.ProvisionAsync(space);
        await stack.SeedMemberAsync(space);

        using var response = await stack.SendAsync(HttpMethod.Post, Accounts, space.Member, space.Tenant, EuroBody);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    // ---- Story 2: see the accounts and the limit use ----

    [Fact]
    public async Task The_list_shows_accounts_balances_and_the_limit_use_and_a_member_reads_but_the_roles_are_not_mixed_up()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        await stack.ProvisionAsync(space);
        await stack.SeedMemberAsync(space);
        using (await stack.SendAsync(HttpMethod.Post, Accounts, space.Owner, space.Tenant,
            """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":125000,"currency":"EUR"},"openingDate":"2026-12-01"}"""))
        {
        }

        string accountId;
        using (var second = await stack.SendAsync(HttpMethod.Post, Accounts, space.Owner, space.Tenant,
            """{"name":"Travel","currency":"JPY","openingBalance":{"amountMinor":5000,"currency":"JPY"},"openingDate":"2026-12-01"}"""))
        {
            second.StatusCode.Should().Be(HttpStatusCode.Created);
            using var created = await JsonAsync(second);
            accountId = created.RootElement.GetProperty("id").GetGuid().ToString("D");
        }

        using var list = await stack.SendAsync(HttpMethod.Get, Accounts, space.Member, space.Tenant);

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        list.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = await JsonAsync(list);
        var accounts = body.RootElement.GetProperty("accounts").EnumerateArray().ToList();
        accounts.Should().HaveCount(2);
        accounts.Select(a => a.GetProperty("currency").GetString()).Should().BeEquivalentTo("EUR", "JPY");
        accounts.Single(a => a.GetProperty("currency").GetString() == "EUR").GetProperty("balance").GetProperty("amountMinor").GetInt64().Should().Be(125000);
        accounts.Single(a => a.GetProperty("currency").GetString() == "JPY").GetProperty("balance").GetProperty("amountMinor").GetInt64().Should().Be(5000);
        body.RootElement.GetProperty("usage").GetProperty("used").GetInt32().Should().Be(2);
        body.RootElement.GetProperty("usage").GetProperty("limit").GetInt32().Should().Be(2);

        using var read = await stack.SendAsync(HttpMethod.Get, $"{Accounts}/{accountId}", space.Member, space.Tenant);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        read.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var readBody = await JsonAsync(read);
        readBody.RootElement.EnumerateObject().Select(p => p.Name).Should().NotContain(["tenantId", "role", "permissions", "canCreate"]);
    }

    [Fact]
    public async Task An_empty_space_lists_no_account_and_shows_the_limit()
    {
        await using var stack = await StartAsync();
        var space = Space.New();

        using var list = await stack.SendAsync(HttpMethod.Get, Accounts, space.Owner, space.Tenant);

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = await JsonAsync(list);
        body.RootElement.GetProperty("accounts").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("usage").GetProperty("used").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("usage").GetProperty("limit").GetInt32().Should().Be(2);
    }

    // ---- Story 3 and Story 4: the third account, and nothing lifts the limit before EP-03 ----

    [Fact]
    public async Task A_third_account_is_refused_with_a_message_that_names_the_limit()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        await CreateAsync(stack, space, "One");
        await CreateAsync(stack, space, "Two");
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records");

        using var third = await stack.SendAsync(HttpMethod.Post, Accounts, space.Owner, space.Tenant, EuroBody);

        await AssertLimitRefusalAsync(third);
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(2);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore, "no audit record for the refused request");
    }

    [Fact]
    public async Task The_limit_counts_the_spaces_accounts_only()
    {
        await using var stack = await StartAsync();
        var spaceA = Space.New();
        var spaceB = Space.New();
        await CreateAsync(stack, spaceA, "A one");
        await CreateAsync(stack, spaceA, "A two");

        using var response = await stack.SendAsync(HttpMethod.Post, Accounts, spaceB.Owner, spaceB.Tenant, EuroBody);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_Pro_trial_or_an_admin_override_does_not_lift_the_limit_before_EP_03()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        await CreateAsync(stack, space, "One");
        await CreateAsync(stack, space, "Two");

        using (var trial = await stack.AdminAsync(HttpMethod.Post, $"/api/admin/tenants/{space.Tenant}/trial"))
        {
            trial.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (var grant = await stack.AdminAsync(
            HttpMethod.Put, $"/api/admin/tenants/{space.Tenant}/overrides/forecasting.scenarios", """{"reason":"Pilot"}"""))
        {
            grant.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var third = await stack.SendAsync(HttpMethod.Post, Accounts, space.Owner, space.Tenant, EuroBody);

        await AssertLimitRefusalAsync(third);
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(2);
    }

    // ---- Story 5: rename and the currency ----

    [Fact]
    public async Task The_owner_renames_an_account_and_a_member_cannot()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        var id = await CreateAsync(stack, space, "Everyday");
        await stack.SeedMemberAsync(space);

        using (var forbidden = await stack.SendAsync(HttpMethod.Put, $"{Accounts}/{id}/name", space.Member, space.Tenant, """{"name":"Hijacked"}"""))
        {
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await stack.ScalarAsync("SELECT name FROM ledger.accounts")).Should().Be("Everyday");
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records");

        using var renamed = await stack.SendAsync(HttpMethod.Put, $"{Accounts}/{id}/name", space.Owner, space.Tenant, """{"name":"Household"}""");

        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = await JsonAsync(renamed);
        body.RootElement.GetProperty("name").GetString().Should().Be("Household");
        body.RootElement.GetProperty("currency").GetString().Should().Be("EUR");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore + 1);
    }

    public static TheoryData<string> InvalidRenames => new()
    {
        """{"name":""}""",
        """{"name":"   "}""",
        $$"""{"name":"{{new string('x', 81)}}"}""",
        """{"title":"Household"}""",
    };

    [Theory]
    [MemberData(nameof(InvalidRenames))]
    public async Task A_rename_with_an_invalid_name_is_refused_and_the_name_is_unchanged(string request)
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        var id = await CreateAsync(stack, space, "Everyday");
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records");

        using var response = await stack.SendAsync(HttpMethod.Put, $"{Accounts}/{id}/name", space.Owner, space.Tenant, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await stack.ScalarAsync("SELECT name FROM ledger.accounts")).Should().Be("Everyday");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore);
    }

    [Fact]
    public async Task The_currency_can_change_while_the_account_holds_no_transaction_and_must_restate_the_opening_balance()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        var id = await CreateAsync(
            stack,
            space,
            "Everyday",
            """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":125000,"currency":"EUR"},"openingDate":"2026-12-01"}""");
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records");

        // Not restating the opening balance (the members absent) is refused and the account is unchanged.
        using (var missing = await stack.SendAsync(HttpMethod.Put, $"{Accounts}/{id}/currency", space.Owner, space.Tenant, """{"currency":"JPY"}"""))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // The restated balance must be in the new currency.
        using (var mismatch = await stack.SendAsync(
            HttpMethod.Put,
            $"{Accounts}/{id}/currency",
            space.Owner,
            space.Tenant,
            """{"currency":"JPY","openingBalance":{"amountMinor":125000,"currency":"EUR"},"openingDate":"2026-12-01"}"""))
        {
            mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // The same currency is no change.
        using (var same = await stack.SendAsync(
            HttpMethod.Put, $"{Accounts}/{id}/currency", space.Owner, space.Tenant, """{"currency":"EUR","openingBalance":null,"openingDate":null}"""))
        {
            same.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var sameBody = await JsonAsync(same);
            sameBody.RootElement.GetProperty("code").GetString().Should().Be("ledger.currency_unchanged");
        }

        (await stack.ScalarAsync("SELECT currency || '/' || opening_balance_minor::text FROM ledger.accounts")).Should().Be("EUR/125000");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore);

        using var changed = await stack.SendAsync(
            HttpMethod.Put,
            $"{Accounts}/{id}/currency",
            space.Owner,
            space.Tenant,
            """{"currency":"JPY","openingBalance":{"amountMinor":5000,"currency":"JPY"},"openingDate":"2026-12-01"}""");

        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = await JsonAsync(changed);
        body.RootElement.GetProperty("currency").GetString().Should().Be("JPY");
        body.RootElement.GetProperty("openingBalance").GetProperty("amountMinor").GetInt64().Should().Be(5000);
        (await stack.ScalarAsync("SELECT currency || '/' || opening_balance_minor::text FROM ledger.accounts")).Should().Be("JPY/5000");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore + 1);
    }

    [Fact]
    public async Task A_member_cannot_change_an_accounts_currency()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        var id = await CreateAsync(stack, space, "Everyday");
        await stack.SeedMemberAsync(space);

        using var response = await stack.SendAsync(
            HttpMethod.Put, $"{Accounts}/{id}/currency", space.Member, space.Tenant, """{"currency":"JPY","openingBalance":null,"openingDate":null}""");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stack.ScalarAsync("SELECT currency FROM ledger.accounts")).Should().Be("EUR");
    }

    // ---- Story 6: spaces are isolated and every read and write is authorised per object ----

    [Fact]
    public async Task A_request_for_another_spaces_account_is_indistinguishable_from_a_missing_one()
    {
        await using var stack = await StartAsync();
        var spaceA = Space.New();
        var spaceB = Space.New();
        var idOfA = await CreateAsync(stack, spaceA, "A's account");
        await stack.ProvisionAsync(spaceB);
        var missing = Guid.CreateVersion7().ToString("D");

        foreach (var (method, suffix, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, "", null),
            (HttpMethod.Put, "/name", """{"name":"Taken over"}"""),
            (HttpMethod.Put, "/currency", """{"currency":"JPY","openingBalance":null,"openingDate":null}"""),
        })
        {
            using var foreign = await stack.SendAsync(method, $"{Accounts}/{idOfA}{suffix}", spaceB.Owner, spaceB.Tenant, body);
            using var absent = await stack.SendAsync(method, $"{Accounts}/{missing}{suffix}", spaceB.Owner, spaceB.Tenant, body);

            foreign.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {suffix}");
            absent.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {suffix}");
            (await ShapeAsync(foreign)).Should().Be(await ShapeAsync(absent), "the two answers carry the same members and values");
        }

        // A malformed id is the same generic 404.
        using var malformed = await stack.SendAsync(HttpMethod.Get, $"{Accounts}/not-an-id", spaceB.Owner, spaceB.Tenant);
        malformed.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await stack.ScalarAsync("SELECT name FROM ledger.accounts")).Should().Be("A's account");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(1, "only the create of space A");
    }

    [Fact]
    public async Task A_list_shows_only_the_callers_own_space()
    {
        await using var stack = await StartAsync();
        var spaceA = Space.New();
        var spaceB = Space.New();
        await CreateAsync(stack, spaceA, "A one");
        await CreateAsync(stack, spaceA, "A two");
        await CreateAsync(stack, spaceB, "B one");

        using var list = await stack.SendAsync(HttpMethod.Get, Accounts, spaceB.Owner, spaceB.Tenant);

        using var body = await JsonAsync(list);
        var names = body.RootElement.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        names.Should().Equal("B one");
        body.RootElement.GetProperty("usage").GetProperty("used").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("usage").GetProperty("limit").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_caller_with_no_tenant_or_no_membership_is_refused_before_any_ledger_query()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        await CreateAsync(stack, space, "Everyday");

        // A caller whose token names no tenant (a platform admin without a space).
        using (var noTenant = await stack.SendAsync(HttpMethod.Get, Accounts, Canaries.Unique("nobody"), null))
        {
            noTenant.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
            (await noTenant.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("Everyday");
        }

        // A caller who is signed in but is not a member of the space the token names.
        using (var stranger = await stack.SendAsync(HttpMethod.Get, Accounts, Canaries.Unique("stranger"), space.Tenant))
        {
            stranger.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await stranger.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("Everyday");
        }

        using (var create = await stack.SendAsync(HttpMethod.Post, Accounts, Canaries.Unique("stranger"), space.Tenant, EuroBody))
        {
            create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(1);
    }

    [Fact]
    public async Task A_request_without_a_token_is_refused()
    {
        await using var stack = await StartAsync();

        using var anonymous = await stack.Client.GetAsync(Accounts, TestContext.Current.CancellationToken);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Story 7: the account and its audit record are written together ----

    [Fact]
    public async Task Each_successful_write_leaves_one_audit_record_with_the_account_id_and_no_name_or_amount()
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        var name = "Canary-" + Guid.NewGuid().ToString("N")[..8];
        var id = await CreateAsync(
            stack, space, name, $$"""{"name":"{{name}}","currency":"EUR","openingBalance":{"amountMinor":918273645,"currency":"EUR"},"openingDate":"2026-12-01"}""");
        using (await stack.SendAsync(HttpMethod.Put, $"{Accounts}/{id}/name", space.Owner, space.Tenant, $$"""{"name":"{{name}} renamed"}""")) { }
        using (await stack.SendAsync(
            HttpMethod.Put, $"{Accounts}/{id}/currency", space.Owner, space.Tenant,
            """{"currency":"JPY","openingBalance":{"amountMinor":918273645,"currency":"JPY"},"openingDate":"2026-12-01"}""")) { }

        (await stack.QueryAsync("SELECT action FROM audit.audit_records ORDER BY id"))
            .Should().Equal("ledger.account.create", "ledger.account.rename", "ledger.account.currency_change");
        (await stack.CountAsync($"SELECT count(*) FROM audit.audit_records WHERE subject_id = '{id}' AND tenant_id = '{space.Tenant}'")).Should().Be(3);
        (await stack.QueryAsync("SELECT t::text FROM audit.audit_records t")).Should()
            .NotContain(t => t.Contains(name, StringComparison.Ordinal) || t.Contains("918273645", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static async Task AssertLimitRefusalAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "a stable error, never a 5xx");
        using var body = await JsonAsync(response);
        body.RootElement.GetProperty("code").GetString().Should().Be("ledger.account_limit_reached");
        body.RootElement.GetProperty("limit").GetInt32().Should().Be(2);
        body.RootElement.GetProperty("detail").GetString().Should().Contain("2", "the message names the limit");
    }

    /// <summary>Status plus every member except the per-request trace id, so two refusals can be compared.</summary>
    private static async Task<string> ShapeAsync(HttpResponseMessage response)
    {
        using var body = await JsonAsync(response);
        var members = body.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .Select(p => $"{p.Name}={p.Value.GetRawText()}");
        return $"{(int)response.StatusCode}|{string.Join(",", members)}";
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    /// <summary>Creates an account as the owner and returns its id (the request body defaults to a EUR account named <paramref name="name"/>).</summary>
    private static async Task<string> CreateAsync(Stack stack, Space space, string name, string? body = null)
    {
        using var response = await stack.SendAsync(
            HttpMethod.Post, Accounts, space.Owner, space.Tenant, body ?? $$"""{"name":"{{name}}","currency":"EUR"}""");
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var created = await JsonAsync(response);
        return created.RootElement.GetProperty("id").GetGuid().ToString("D");
    }

    private async Task<Stack> StartAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFullyMigratedDatabaseAsync(ct);

        var ledger = new DbContextOptionsBuilder<LedgerDbContext>();
        LedgerDbContextOptions.Configure(ledger, connectionString);
        await using (var db = new LedgerDbContext(ledger.Options, new TestCurrentTenant()))
        {
            await db.Database.MigrateAsync(ct);
        }

        var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            extraConfiguration:
            [
                new("ConnectionStrings:tenancy", connectionString),
                new("ConnectionStrings:entitlements", connectionString),
                new("ConnectionStrings:ledger", connectionString),
            ]);
        return new Stack(factory, factory.CreateClient(), _issuer, connectionString);
    }

    private sealed record Space(string Tenant, string Owner, string Member)
    {
        public static Space New() => new(Guid.NewGuid().ToString("D"), Canaries.Unique("owner"), Canaries.Unique("member"));
    }

    private sealed class Stack(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        HttpClient client,
        TestTokenIssuer issuer,
        string connectionString) : IAsyncDisposable
    {
        public HttpClient Client => client;

        /// <summary>The owner's first request: JIT provisioning creates the space and the Owner membership.</summary>
        public async Task ProvisionAsync(Space space)
        {
            using var response = await SendAsync(HttpMethod.Get, Accounts, space.Owner, space.Tenant);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        /// <summary>Seeds a Member row directly: JIT provisioning only ever assigns Owner, and no invite path exists (G1 out of scope).</summary>
        public async Task SeedMemberAsync(Space space)
        {
            await ProvisionAsync(space);
            var tenantId = TenantId.Parse(space.Tenant);
            var options = new DbContextOptionsBuilder<TenancyDbContext>();
            TenancyDbContextOptions.Configure(options, connectionString);
            await using var db = new TenancyDbContext(options.Options, new TestCurrentTenant { Resolution = TenantResolution.For(tenantId) });
            db.Memberships.Add(new Membership(tenantId, space.Member, TenantRole.Member, NodaTime.SystemClock.Instance.GetCurrentInstant()));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string path, string subject, string? tenant, string? body = null, string contentType = "application/json")
        {
            var token = issuer.IssueAccessToken(subject, DecisyaTestTokens.Tenant(tenant));
            return SendWithTokenAsync(method, path, token, body, contentType);
        }

        /// <summary>Sends a body with no Content-Type header at all (a raw byte body, no media type).</summary>
        public async Task<HttpResponseMessage> SendWithoutContentTypeAsync(
            HttpMethod method, string path, string subject, string? tenant, string body)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", issuer.IssueAccessToken(subject, DecisyaTestTokens.Tenant(tenant)));
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = null;

            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, string? body = null)
        {
            var claims = issuer.DefaultClaims(subject: "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59");
            claims["roles"] = new[] { "platform-admin" };
            claims["acr"] = "2"; // #121: the MFA proof the step-up flow gives an admin.
            var token = TestTokenIssuer.IssueToken(claims, issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
            return SendWithTokenAsync(method, path, token, body, "application/json");
        }

        private async Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, string path, string token, string? body, string contentType)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }

            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<List<string>> QueryAsync(string sql)
        {
            var values = new List<string>();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // every call site passes a fixed literal or a string built from test-generated ids.
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                values.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty);
            }

            return values;
        }

        public async Task<string> ScalarAsync(string sql) => (await QueryAsync(sql)).Single();

        public async Task<long> CountAsync(string sql) =>
            long.Parse(await ScalarAsync(sql), CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await factory.DisposeAsync();
        }
    }
}
