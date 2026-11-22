using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Ledger.Infrastructure;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // G6-341-06 (d), G3 M341-05 (d), ADR-0027 points 2 and 3: the strict number form, the closed request shape and the name rules.
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":1e3,"currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":125000.0,"currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":-9007199254740992,"currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.amount_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":null,"currency":"EUR"},"openingDate":"2026-12-01"}""", "ledger.body_invalid" },
        { """{"name":"Everyday"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":100},"openingDate":"2026-12-01"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","Name":"Other","currency":"EUR"}""", "ledger.body_invalid" },
        { """{"name":"Everyday","currency":"EUR","currencyLocked":false}""", "ledger.body_invalid" },
        { """{"name":"a\u0000b","currency":"EUR"}""", "ledger.name_invalid" },
        { """{"name":"\ud800","currency":"EUR"}""", "ledger.body_invalid" },
        { $$"""{"name":"{{new string('x', 5000)}}","currency":"EUR"}""", "ledger.body_invalid" },
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

    // ---- G6-341-06 (d): the body size, the missing Content-Length and the charset ----

    private static readonly string OversizedName = new('x', 5000);

    public static TheoryData<string, string, string> ChunkedWrites => new()
    {
        { "POST", "", $$"""{"name":"{{OversizedName}}","currency":"EUR"}""" },
        { "PUT", "/name", $$"""{"name":"{{OversizedName}}"}""" },
        { "PUT", "/currency", $$"""{"currency":"JPY","openingBalance":null,"openingDate":null,"pad":"{{OversizedName}}"}""" },
    };

    [Theory]
    [MemberData(nameof(ChunkedWrites))]
    public async Task A_body_over_4_KiB_with_no_Content_Length_is_refused_and_nothing_is_stored(string method, string suffix, string body)
    {
        // The filter strips the Content-Length server-side (the test server computes one for any content) and leaves the
        // body-size feature writable, so LimitBody sets MaxRequestBodySize and the read must fail on the size.
        var filter = new ReadOnlyBodyLimitStartupFilter(readOnlyFeature: false);
        await using var stack = await StartAsync(
            configureServices: services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(filter));
        var space = Space.New();
        var id = await CreateAsync(stack, space, "Everyday");
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records");
        var httpMethod = new HttpMethod(method);
        var path = httpMethod == HttpMethod.Post ? Accounts : $"{Accounts}/{id}{suffix}";

        using var response = await stack.SendChunkedAsync(httpMethod, path, space.Owner, space.Tenant, body);

        // Preconditions: the middleware ran for the marked request, handed it on with no Content-Length, with a writable feature.
        var seen = filter.Observed.Where(line => line.Contains("Stripped=True", StringComparison.Ordinal)).ToList();
        seen.Should().ContainSingle("the startup filter's middleware must have run for the marked request (observed: [{0}])", string.Join(" ; ", filter.Observed));
        seen[0].Should().Contain("ContentLength=null", "the request must reach the endpoint without a Content-Length");
        seen[0].Should().Contain("FeaturePresent=True").And.Contain("ReadOnly=False", "the size limit must be writable, so MaxRequestBodySize is what refuses");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = await JsonAsync(response);
        problem.RootElement.GetProperty("code").GetString().Should().Be("ledger.body_invalid");
        (await stack.ScalarAsync("SELECT name || '/' || currency FROM ledger.accounts")).Should().Be("Everyday/EUR");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore, "no audit record for the refused request");
    }

    /// <summary>
    /// The refuse-when-unenforceable branch of <c>LimitBody</c>: a startup filter replaces the request's
    /// <c>IHttpMaxRequestBodySizeFeature</c> with a read-only one, so a body without a <c>Content-Length</c> cannot
    /// be limited and is refused, even a small one. (The test server's own feature is writable, so without the
    /// filter the small body is read and the create succeeds.)
    /// </summary>
    [Fact]
    public async Task A_small_body_with_no_Content_Length_is_refused_when_the_host_cannot_enforce_the_limit()
    {
        var filter = new ReadOnlyBodyLimitStartupFilter();
        await using var stack = await StartAsync(
            configureServices: services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(filter));
        var space = Space.New();
        await stack.ProvisionAsync(space);

        using var response = await stack.SendChunkedAsync(HttpMethod.Post, Accounts, space.Owner, space.Tenant, EuroBody);

        // Preconditions: the middleware ran for the marked POST, handed it on with no Content-Length, and the feature was read-only.
        var seen = filter.Observed.Where(line => line.Contains("Stripped=True", StringComparison.Ordinal)).ToList();
        seen.Should().ContainSingle("the startup filter's middleware must have run for the marked POST (observed: [{0}])", string.Join(" ; ", filter.Observed));
        seen[0].Should().Contain("ContentLength=null", "the request must reach the endpoint without a Content-Length");
        seen[0].Should().Contain("ReadOnly=True", "the feature must be read-only after the swap");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = await JsonAsync(response);
        problem.RootElement.GetProperty("code").GetString().Should().Be("ledger.body_invalid");
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    [Theory]
    [InlineData("POST", "", EuroBody)]
    [InlineData("PUT", "/name", """{"name":"Household"}""")]
    [InlineData("PUT", "/currency", """{"currency":"JPY","openingBalance":null,"openingDate":null}""")]
    public async Task A_charset_other_than_utf_8_is_refused_before_the_body_is_read_and_nothing_is_stored(string method, string suffix, string body)
    {
        await using var stack = await StartAsync();
        var space = Space.New();
        var id = await CreateAsync(stack, space, "Everyday");
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records");
        var httpMethod = new HttpMethod(method);
        var path = httpMethod == HttpMethod.Post ? Accounts : $"{Accounts}/{id}{suffix}";

        foreach (var contentType in new[] { "application/json; charset=utf-16", "application/json; charset=iso-8859-1" })
        {
            using var response = await stack.SendAsync(httpMethod, path, space.Owner, space.Tenant, body, contentType);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, contentType);
            using var problem = await JsonAsync(response);
            problem.RootElement.GetProperty("code").GetString().Should().Be("ledger.body_invalid", contentType);
        }

        (await stack.ScalarAsync("SELECT name || '/' || currency FROM ledger.accounts")).Should().Be("Everyday/EUR");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(auditBefore);
    }

    [Fact]
    public async Task An_explicit_utf_8_charset_is_accepted()
    {
        await using var stack = await StartAsync();
        var space = Space.New();

        using var response = await stack.SendAsync(
            HttpMethod.Post, Accounts, space.Owner, space.Tenant, EuroBody, "application/json; charset=UTF-8");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- G6-341-05 (d): parallel creates over HTTP ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Parallel_creates_through_the_real_host_never_pass_the_limit_and_never_return_a_5xx(int existing)
    {
        const int parallel = 16;
        await using var stack = await StartAsync();
        var space = Space.New();

        // Provision the space first, as the other tests do: this test proves the limit under concurrency, not
        // concurrent first-request provisioning (which fails closed with 401; a backlog discovery).
        await stack.ProvisionAsync(space);
        for (var i = 0; i < existing; i++)
        {
            await CreateAsync(stack, space, $"Seed {i}");
        }

        var allowed = 2 - existing;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, parallel).Select(async i =>
        {
            await go.Task;
            using var response = await stack.SendAsync(
                HttpMethod.Post, Accounts, space.Owner, space.Tenant, $$"""{"name":"Parallel {{i}}","currency":"EUR"}""");
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            string? code = null;
            if (response.StatusCode != HttpStatusCode.Created)
            {
                try
                {
                    using var problem = JsonDocument.Parse(text);
                    code = problem.RootElement.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : "(no code)";
                }
                catch (JsonException)
                {
                    code = "(not JSON)";
                }
            }

            return (response.StatusCode, Code: code);
        }).ToList();
        go.SetResult();
        var results = await Task.WhenAll(calls);

        // Name every response that is neither a 201 nor the limit 409 (status and problem code), so a failure shows what it was.
        var unexpected = string.Join(
            "; ",
            results.Where(r => r.StatusCode != HttpStatusCode.Created && !(r.StatusCode == HttpStatusCode.Conflict && r.Code == "ledger.account_limit_reached"))
                .Select(r => $"{(int)r.StatusCode} {r.Code}"));

        results.Where(r => (int)r.StatusCode >= 500).Should().BeEmpty("a surplus create is a stable 409, never a 5xx; unexpected responses: [{0}]", unexpected);
        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(allowed, "unexpected responses: [{0}]", unexpected);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict && r.Code == "ledger.account_limit_reached")
            .Should().Be(parallel - allowed, "unexpected responses: [{0}]", unexpected);
        (await stack.CountAsync("SELECT count(*) FROM ledger.accounts")).Should().Be(2);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records WHERE action = 'ledger.account.create'")).Should().Be(2);
    }

    // ---- G6-341-02: NFR-87 and F341-02, the canaries ----

    private const string NameCanary = "MARKER-7c1e";
    private const string AmountCanary = "918273645";

    /// <summary>
    /// NFR-87 (G3 F341-02, T341-16): an account name and an opening amount never reach a log record (message, state,
    /// scopes, exception text), a span (name, tags, events) or a metric tag, on the success paths, on the refusal
    /// paths and on the three failure paths (a refused body whose unknown member is the canary, a failing audit
    /// append, a failing <c>SaveChanges</c> whose Postgres error would carry the row in its detail), through the real
    /// host so the exception handler's own log is captured. The metric tags are exactly the three bounded ones.
    /// </summary>
    [Fact]
    public async Task The_account_name_and_the_opening_amount_never_reach_a_log_a_span_a_metric_tag_or_a_problem_body()
    {
        var logs = new ScopeAwareLogCapture();
        var faults = new AuditFaultSwitch();
        await using var stack = await StartAsync(
            logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddFilter<ScopeAwareLogCapture>(null, LogLevel.Trace);
                logging.AddProvider(logs);
            },
            services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAuditWriter));
                var implementation = original.ImplementationType
                    ?? throw new InvalidOperationException("The audit writer registration has no implementation type.");
                services.Remove(original);
                services.AddScoped<IAuditWriter>(sp => new ArmableAuditWriter((IAuditWriter)ActivatorUtilities.CreateInstance(sp, implementation), faults));
            });

        // Failure path 2 setup, before the telemetry capture starts so that the statement's own span (db.query.text)
        // is not in the scan: a check constraint (not validated, so it only meets new and changed rows) that rejects
        // a name ending "save" with the canary. Postgres' error detail for it would carry the failing row. The success
        // and refusal paths use other names, so they are not affected.
        await stack.QueryAsync(
            "ALTER TABLE ledger.accounts ADD CONSTRAINT ck_canary_fault CHECK (name NOT LIKE '%MARKER-7c1e save%') NOT VALID");
        using var telemetry = new Telemetry();

        var space = Space.New();
        var problems = new List<string>();
        var statuses = new List<HttpStatusCode>();

        async Task<string> SendAsync(HttpMethod method, string path, Space who, string? body = null)
        {
            using var response = await stack.SendAsync(method, path, who.Owner, who.Tenant, body);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
            problems.AddRange(response.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
            if ((int)response.StatusCode >= 400)
            {
                problems.Add(text);
            }

            return text;
        }

        string Create(string name) =>
            $$"""{"name":"{{name}}","currency":"EUR","openingBalance":{"amountMinor":{{AmountCanary}},"currency":"EUR"},"openingDate":"2026-12-01"}""";

        // Success paths: create, rename, currency change, list, read.
        var created = await SendAsync(HttpMethod.Post, Accounts, space, Create($"{NameCanary} one"));
        using var createdBody = JsonDocument.Parse(created);
        var id = createdBody.RootElement.GetProperty("id").GetGuid().ToString("D");
        await SendAsync(HttpMethod.Put, $"{Accounts}/{id}/name", space, $$"""{"name":"{{NameCanary}} renamed"}""");
        await SendAsync(
            HttpMethod.Put, $"{Accounts}/{id}/currency", space,
            $$"""{"currency":"JPY","openingBalance":{"amountMinor":{{AmountCanary}},"currency":"JPY"},"openingDate":"2026-12-01"}""");
        await SendAsync(HttpMethod.Get, Accounts, space);
        await SendAsync(HttpMethod.Get, $"{Accounts}/{id}", space);

        // Refusal paths: an invalid name, the limit, an unchanged currency, a missing id, a refused body whose unknown member is the canary.
        await SendAsync(HttpMethod.Post, Accounts, space, Create($"{NameCanary}\\u0000"));
        await SendAsync(HttpMethod.Post, Accounts, space, Create($"{NameCanary} two"));
        await SendAsync(HttpMethod.Post, Accounts, space, Create($"{NameCanary} three"));
        await SendAsync(
            HttpMethod.Put, $"{Accounts}/{id}/currency", space,
            $$"""{"currency":"JPY","openingBalance":{"amountMinor":{{AmountCanary}},"currency":"JPY"},"openingDate":"2026-12-01"}""");
        await SendAsync(HttpMethod.Put, $"{Accounts}/{Guid.CreateVersion7():D}/name", space, $$"""{"name":"{{NameCanary}}"}""");
        await SendAsync(
            HttpMethod.Post, Accounts, space,
            $$"""{"name":"{{NameCanary}}","currency":"EUR","{{NameCanary}}":{{AmountCanary}},"openingBalance":{"amountMinor":{{AmountCanary}},"currency":"EUR"},"openingDate":"2026-12-01"}""");
        await SendAsync(HttpMethod.Put, $"{Accounts}/{id}/name", space, $$"""{"name":"{{NameCanary}}","{{NameCanary}}":{{AmountCanary}}}""");

        // Failure path 1: the audit append fails after the real append (the transaction rolls back, the host answers 500).
        var failing = Space.New();
        await stack.ProvisionAsync(failing);
        faults.Armed = true;
        using (var createFailedAudit = await stack.SendAsync(HttpMethod.Post, Accounts, failing.Owner, failing.Tenant, Create($"{NameCanary} audit")))
        {
            createFailedAudit.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            problems.Add(await createFailedAudit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var renameFailed = await stack.SendAsync(
            HttpMethod.Put, $"{Accounts}/{id}/name", space.Owner, space.Tenant, $$"""{"name":"{{NameCanary}} audit"}"""))
        {
            renameFailed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            problems.Add(await renameFailed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        faults.Armed = false;

        // Failure path 2: SaveChanges fails on the constraint added above.
        var third = Space.New();
        await stack.ProvisionAsync(third);
        using (var createFailed = await stack.SendAsync(HttpMethod.Post, Accounts, third.Owner, third.Tenant, Create($"{NameCanary} save")))
        {
            createFailed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            problems.Add(await createFailed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var renameFailed = await stack.SendAsync(
            HttpMethod.Put, $"{Accounts}/{id}/name", space.Owner, space.Tenant, $$"""{"name":"{{NameCanary}} save"}"""))
        {
            renameFailed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            problems.Add(await renameFailed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // The scan is not vacuous: the paths ran and the capture saw them.
        statuses.Should().Contain([HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.NotFound]);
        logs.Records.Should().Contain(r => r.Category.Contains("Ledger", StringComparison.Ordinal), "the Ledger's own log lines were captured");
        logs.Records.Should().Contain(r => r.ExceptionText != null, "the exception handler's log of a 500 was captured");
        telemetry.Values.Should().Contain("decisya.ledger.operation=create");
        telemetry.Values.Should().Contain(v => v.StartsWith("Ledger.", StringComparison.Ordinal), "the module's spans were captured");

        foreach (var canary in new[] { NameCanary, AmountCanary })
        {
            problems.Should().NotContain(s => s.Contains(canary, StringComparison.Ordinal), $"no refusal or failure body, and no header, may contain {canary}");
            logs.Records.Should().NotContain(r => r.Contains(canary), $"no log record (message, state, scopes, exception) may contain {canary}");
            telemetry.Values.Should().NotContain(v => v.Contains(canary, StringComparison.Ordinal), $"no span name, tag or event and no metric tag may contain {canary}");
        }

        // The Ledger's metric tags are exactly the three bounded ones; no tag carries an account id, a tenant id or an amount.
        var ledgerPoints = telemetry.MetricPoints.Where(p => p.Meter == "Decisya.Ledger").ToList();
        ledgerPoints.Should().NotBeEmpty();
        string[] operations = ["list", "get", "create", "rename", "currency_change"];
        string[] results = ["ok", "refused", "error"];
        string[] reasons = ["none", "invalid", "forbidden", "not_found", "limit_reached", "currency_locked", "currency_unchanged"];
        foreach (var point in ledgerPoints)
        {
            point.Tags.Keys.Should().BeEquivalentTo(["decisya.ledger.operation", "result", "decisya.ledger.reason"], $"{point.Instrument}");
            operations.Should().Contain(point.Tags["decisya.ledger.operation"]);
            results.Should().Contain(point.Tags["result"]);
            reasons.Should().Contain(point.Tags["decisya.ledger.reason"]);
        }

        telemetry.MetricPoints.SelectMany(p => p.Tags.Values).Should()
            .NotContain(v => v.Contains(id, StringComparison.OrdinalIgnoreCase) || v.Contains(space.Tenant, StringComparison.OrdinalIgnoreCase),
                "no metric tag of any meter carries the account id or the tenant id");
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

    private async Task<Stack> StartAsync(
        Action<ILoggingBuilder>? configureLogging = null, Action<IServiceCollection>? configureServices = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFullyMigratedDatabaseAsync(ct);

        var ledger = new DbContextOptionsBuilder<LedgerDbContext>();
        LedgerDbContextOptions.Configure(ledger, connectionString);
        await using (var db = new LedgerDbContext(ledger.Options, new TestCurrentTenant()))
        {
            await db.Database.MigrateAsync(ct);
        }

        // The host's own pool is bounded (the shared test server allows 100 connections for every database and class),
        // and both pools are cleared when the stack is disposed, so no stack leaves idle connections behind.
        var apiConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 24 }.ConnectionString;

        var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: configureServices,
            extraConfiguration:
            [
                new("ConnectionStrings:tenancy", apiConnectionString),
                new("ConnectionStrings:entitlements", apiConnectionString),
                new("ConnectionStrings:ledger", apiConnectionString),
            ],
            configureLogging: configureLogging);
        return new Stack(factory, factory.CreateClient(), _issuer, connectionString, apiConnectionString);
    }

    private sealed record Space(string Tenant, string Owner, string Member)
    {
        public static Space New() => new(Guid.NewGuid().ToString("D"), Canaries.Unique("owner"), Canaries.Unique("member"));
    }

    private sealed class Stack(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        HttpClient client,
        TestTokenIssuer issuer,
        string connectionString,
        string apiConnectionString) : IAsyncDisposable
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

        /// <summary>Sends a body with no Content-Length (an unknown-length stream content, so the request is chunked).</summary>
        public async Task<HttpResponseMessage> SendChunkedAsync(
            HttpMethod method, string path, string subject, string? tenant, string body)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", issuer.IssueAccessToken(subject, DecisyaTestTokens.Tenant(tenant)));
            request.Headers.Add(ReadOnlyBodyLimitStartupFilter.StripContentLengthHeader, "1");
            request.Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

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

            // Close the idle connections of this stack's database: Npgsql pools outlive the host and the shared
            // Postgres allows only 100 connections ("53300 too many clients") across every stack of the assembly.
            foreach (var pooled in new[] { connectionString, apiConnectionString })
            {
                await using var connection = new NpgsqlConnection(pooled);
                NpgsqlConnection.ClearPool(connection);
            }
        }
    }

    /// <summary>Runs first in the pipeline and swaps the request's body-size feature for a read-only one, as a server that cannot set the limit.</summary>
    private sealed class ReadOnlyBodyLimitStartupFilter(bool readOnlyFeature = true) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        /// <summary>The request header a test sets to ask for a server-side chunked request (the test server computes a Content-Length for any content).</summary>
        public const string StripContentLengthHeader = "X-Test-Strip-Content-Length";

        /// <summary>One line per request the middleware saw: path, whether it was marked, <c>Content-Length</c> as handed on, and the feature's read-only state.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Observed { get; } = new();

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    var marked = context.Request.Headers.ContainsKey(StripContentLengthHeader);
                    if (marked)
                    {
                        // As a chunked request arrives at Kestrel: no Content-Length at all.
                        context.Request.Headers.Remove(StripContentLengthHeader);
                        context.Request.ContentLength = null;
                    }

                    if (readOnlyFeature)
                    {
                        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(new ReadOnlyBodySizeFeature());
                    }

                    var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                    Observed.Enqueue(
                        $"{context.Request.Method} {context.Request.Path}|Stripped={marked}|ContentLength={context.Request.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "null"}|FeaturePresent={feature is not null}|ReadOnly={feature?.IsReadOnly}");
                    await nextMiddleware();
                });
                next(app);
            };
    }

    private sealed class ReadOnlyBodySizeFeature : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => true;

        public long? MaxRequestBodySize
        {
            get => null;
            set => throw new InvalidOperationException("The limit is read-only.");
        }
    }

    /// <summary>A body whose length is unknown, so the HTTP client sends no <c>Content-Length</c>.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, TestContext.Current.CancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Armed, the writer appends for real and then throws, as a failing audit append after the account write.</summary>
    private sealed class AuditFaultSwitch
    {
        private volatile bool _armed;

        public bool Armed
        {
            get => _armed;
            set => _armed = value;
        }
    }

    private sealed class ArmableAuditWriter(IAuditWriter inner, AuditFaultSwitch faults) : IAuditWriter
    {
        public async Task AppendAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken = default)
        {
            await inner.AppendAsync(entry, transaction, cancellationToken);
            if (faults.Armed)
            {
                throw new InvalidOperationException("Injected fault after a real audit append.");
            }
        }
    }

    /// <summary>A log provider that keeps every record with its structured state and every scope open at the call.</summary>
    private sealed class ScopeAwareLogCapture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<CapturedLogRecord> Records { get; } = new();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

        public void Dispose()
        {
        }

        private static string Stringify(object? state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : state?.ToString() ?? string.Empty;

        private sealed class CaptureLogger(ScopeAwareLogCapture provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                var scopeText = new StringBuilder();
                provider._scopes.ForEachScope((scope, text) => text.Append(Stringify(scope)).Append(" | "), scopeText);

                provider.Records.Enqueue(new CapturedLogRecord(
                    logLevel,
                    category,
                    formatter(state, exception),
                    $"{Stringify(state)} || scopes: {scopeText}",
                    exception?.ToString(),
                    eventId.Name,
                    eventId.Id));
            }
        }
    }

    /// <summary>Every span name, tag and event and every measurement tag, of any source or meter, as strings.</summary>
    private sealed class Telemetry : IDisposable
    {
        private readonly ConcurrentQueue<string> _values = new();
        private readonly ConcurrentQueue<MetricPoint> _points = new();
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters;

        public Telemetry()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    _values.Enqueue(activity.DisplayName);
                    _values.Enqueue(activity.StatusDescription ?? string.Empty);
                    foreach (var tag in activity.TagObjects)
                    {
                        _values.Enqueue($"{tag.Key}={tag.Value}");
                    }

                    foreach (var activityEvent in activity.Events)
                    {
                        _values.Enqueue(activityEvent.Name);
                        foreach (var tag in activityEvent.Tags)
                        {
                            _values.Enqueue($"{tag.Key}={tag.Value}");
                        }
                    }
                },
            };
            ActivitySource.AddActivityListener(_activities);

            _meters = new MeterListener { InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument) };
            _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
            _meters.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Record(instrument, tags));
            _meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
            _meters.Start();
        }

        public IReadOnlyList<string> Values => [.. _values];

        public IReadOnlyList<MetricPoint> MetricPoints => [.. _points];

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }

        private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString() ?? string.Empty;
                map[tag.Key] = value;
                _values.Enqueue($"{tag.Key}={value}");
            }

            _points.Enqueue(new MetricPoint(instrument.Meter.Name, instrument.Name, map));
        }

        internal sealed record MetricPoint(string Meter, string Instrument, IReadOnlyDictionary<string, string> Tags);
    }
}
