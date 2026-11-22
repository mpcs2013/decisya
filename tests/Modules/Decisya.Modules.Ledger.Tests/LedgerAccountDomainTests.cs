using System.Reflection;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Domain;
using Decisya.SharedKernel;
using Decisya.SharedKernel.Tenancy;
using NetArchTest.Rules;
using NodaTime;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G1 Stories 1, 2, 4 and 5 at the domain level (AC-02-1, AC-02-3, AC-02-4, AC-02-5): the account keeps the money
/// as minor units of its own currency; the balance is the opening balance plus the net of the transactions
/// (the EP-04 seam); the currency changes only while the account holds no transaction and the change restates the
/// opening balance; the limit is one fixed value in code that no configuration, plan, trial or override reads.
/// No database, no host. The stored-value and HTTP halves are in the Integration classes.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LedgerAccountDomainTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 21, 9, 0);
    private static readonly LocalDate OpeningDate = new(2026, 12, 1);

    private static AccountName Name(string text) =>
        AccountName.TryCreate(text, out var name) ? name : throw new InvalidOperationException("Test name is invalid.");

    private static Money Minor(long minor, string code) => Money.FromMinorUnits(minor, Currency.FromCode(code));

    private static Account Open(string code = "EUR", long? minor = 100000) =>
        Account.Open(
            Tenant,
            Name("Everyday"),
            Currency.FromCode(code),
            minor is { } m ? Minor(m, code) : null,
            minor is null ? null : OpeningDate,
            Now);

    // ---- Story 1: the account holds money as minor units in its own currency ----

    [Theory]
    [InlineData("JPY", 5000L)]
    [InlineData("EUR", 125000L)]
    [InlineData("KWD", 1500000L)]
    [InlineData("EUR", -4500L)]
    public void Minor_units_follow_the_currencys_own_exponent(string code, long minor)
    {
        var account = Open(code, minor);

        account.OpeningBalanceMinor.Should().Be(minor);
        account.OpeningBalance.ToMinorUnits().Should().Be(minor);
        account.Currency.Code.Should().Be(code);
        account.OpeningDate.Should().Be(OpeningDate);
    }

    [Fact]
    public void The_opening_balance_is_optional_and_absent_means_zero_in_the_accounts_currency()
    {
        var account = Account.Open(Tenant, Name("Savings"), Currency.FromCode("CHF"), null, null, Now);

        account.OpeningBalanceMinor.Should().Be(0);
        account.OpeningDate.Should().BeNull();
        account.OpeningBalance.ToMinorUnits().Should().Be(0);
        account.OpeningBalance.Currency.Code.Should().Be("CHF");
        account.BalanceWith(0).ToMinorUnits().Should().Be(0);
    }

    [Fact]
    public void An_account_is_tenant_scoped_to_the_space_that_opened_it_and_is_named_as_typed()
    {
        var account = Open();

        account.TenantId.Should().Be(Tenant);
        account.Name.Should().Be("Everyday");
        account.CreatedAt.Should().Be(Now);
        account.Id.Should().NotBeEmpty();
        typeof(Account).Should().Implement<ITenantScoped>();
    }

    [Fact]
    public void An_opening_balance_without_a_date_or_a_date_without_a_balance_is_a_programming_error()
    {
        var withoutDate = () => Account.Open(Tenant, Name("A"), Currency.FromCode("EUR"), Minor(1, "EUR"), null, Now);
        var withoutBalance = () => Account.Open(Tenant, Name("A"), Currency.FromCode("EUR"), null, OpeningDate, Now);

        withoutDate.Should().Throw<ArgumentException>();
        withoutBalance.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_opening_balance_in_another_currency_than_the_account_is_a_programming_error()
    {
        var act = () => Account.Open(Tenant, Name("A"), Currency.FromCode("EUR"), Minor(1, "USD"), OpeningDate, Now);

        act.Should().Throw<ArgumentException>();
    }

    // ---- Story 2: the balance ----

    [Fact]
    public void The_balance_is_the_opening_balance_when_the_account_holds_no_transaction()
    {
        var balance = Open("EUR", 100000).BalanceWith(0);

        balance.ToMinorUnits().Should().Be(100000);
        balance.Currency.Code.Should().Be("EUR");
    }

    [Fact]
    public void The_balance_adds_the_transactions_once_EP_04_provides_them()
    {
        // -2500 and +10000 minor units on an account opened at 100000.
        var balance = Open("EUR", 100000).BalanceWith(-2500 + 10000);

        balance.ToMinorUnits().Should().Be(107500);
        balance.Currency.Code.Should().Be("EUR");
    }

    [Fact]
    public void A_balance_is_never_converted_or_summed_across_currencies()
    {
        var euro = Open("EUR", 125000).BalanceWith(0);
        var yen = Open("JPY", 5000).BalanceWith(0);

        euro.Currency.Code.Should().Be("EUR");
        yen.Currency.Code.Should().Be("JPY");
        var sum = () => euro + yen;
        sum.Should().Throw<Exception>("adding amounts of two currencies is refused by Money itself");
    }

    // ---- Story 5: rename and the currency lock ----

    [Fact]
    public void A_rename_changes_only_the_name()
    {
        var account = Open("EUR", 125000);

        account.Rename(Name("Household"));

        account.Name.Should().Be("Household");
        account.Currency.Code.Should().Be("EUR");
        account.OpeningBalanceMinor.Should().Be(125000);
        account.OpeningDate.Should().Be(OpeningDate);
    }

    [Fact]
    public void The_currency_changes_while_the_account_holds_no_transaction_and_restates_the_opening_balance()
    {
        var account = Open("EUR", 125000);

        var changed = account.TryChangeCurrency(Currency.FromCode("JPY"), Minor(5000, "JPY"), OpeningDate, holdsTransaction: false);

        changed.Should().BeTrue();
        account.Currency.Code.Should().Be("JPY");
        account.OpeningBalanceMinor.Should().Be(5000);
        account.OpeningBalance.Currency.Code.Should().Be("JPY");
    }

    [Fact]
    public void The_currency_cannot_change_once_the_account_holds_a_transaction_and_nothing_else_changes()
    {
        var account = Open("EUR", 125000);

        var changed = account.TryChangeCurrency(Currency.FromCode("USD"), Minor(1, "USD"), OpeningDate, holdsTransaction: true);

        changed.Should().BeFalse();
        account.Currency.Code.Should().Be("EUR");
        account.OpeningBalanceMinor.Should().Be(125000);
    }

    [Fact]
    public void A_rename_of_a_locked_account_is_still_allowed_and_its_currency_stays()
    {
        var account = Open("EUR", 125000);
        account.TryChangeCurrency(Currency.FromCode("USD"), null, null, holdsTransaction: true).Should().BeFalse();

        account.Rename(Name("Renamed"));

        account.Name.Should().Be("Renamed");
        account.Currency.Code.Should().Be("EUR");
    }

    // ---- Story 4: the limit is one fixed value in code ----

    [Fact]
    public void The_limit_is_the_constant_2_and_the_only_member_of_its_type()
    {
        AccountLimits.PerSpace.Should().Be(2);

        var members = typeof(AccountLimits).GetMembers(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        members.Where(m => m.MemberType is MemberTypes.Field or MemberTypes.Property or MemberTypes.Method)
            .Where(m => m.Name != ".ctor")
            .Select(m => m.Name)
            .Should().BeEquivalentTo([nameof(AccountLimits.PerSpace)], "no setter, option or second value exists");
        typeof(AccountLimits).GetField(nameof(AccountLimits.PerSpace), BindingFlags.NonPublic | BindingFlags.Static)!
            .IsLiteral.Should().BeTrue("a const cannot be changed at run time");
    }

    [Fact]
    public void No_configuration_option_plan_or_entitlement_is_read_by_the_ledger_domain_or_application_code()
    {
        var result = Types.InAssembly(typeof(LedgerModule).Assembly)
            .That().ResideInNamespace("Decisya.Modules.Ledger.Domain")
            .Or().ResideInNamespace("Decisya.Modules.Ledger.Application")
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.Extensions.Configuration",
                "Microsoft.Extensions.Options",
                "Decisya.Modules.Entitlements")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("the limit check must read no setting, plan, trial or override");
    }

    [Fact]
    public void The_create_handler_takes_no_configuration_options_or_entitlement_service()
    {
        var parameters = typeof(CreateAccountHandler).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)
            .ToList();

        parameters.Should().NotContain(t => t.Contains("Entitlement", StringComparison.Ordinal));
        parameters.Should().NotContain(t => t.Contains("IConfiguration", StringComparison.Ordinal));
        parameters.Should().NotContain(t => t.Contains("IOptions", StringComparison.Ordinal));
    }

    [Fact]
    public void The_plan_catalogue_holds_no_unlimited_accounts_key_after_this_issue()
    {
        FeatureKeys.All.Select(k => k.Value).Should().BeEquivalentTo("ledger.transactions", "forecasting.scenarios");
        FeatureKeys.All.Select(k => k.Value).Should().NotContain("ledger.accounts.unlimited");
    }
}
