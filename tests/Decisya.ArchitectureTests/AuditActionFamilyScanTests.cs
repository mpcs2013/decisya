namespace Decisya.ArchitectureTests;

/// <summary>
/// G6-341-04 / R-L21 (F341-01; ADR-0013 amendment 1 point 2): each module writes only its own audit action family.
/// The live scan runs over the real <c>src/</c> tree; the red and green rows prove the scan itself, with in-memory
/// files at repository-relative paths (a red fixture file must fail the scan outside the owning module's folder).
/// </summary>
[Trait("Category", "Architecture")]
public class AuditActionFamilyScanTests
{
    private const string LedgerFile = "src/Modules/Ledger/Decisya.Modules.Ledger/Application/CreateAccountHandler.cs";
    private const string EntitlementsFile = "src/Modules/Entitlements/Decisya.Modules.Entitlements/Application/StartTrialHandler.cs";

    [Fact]
    public void Over_the_real_source_tree_each_module_uses_only_its_own_audit_action_family()
    {
        var files = AuditActionFamilyRule.SourceFiles().ToList();

        files.Should().Contain(f => f.RelativePath == LedgerFile, "the scan must see the Ledger handlers, or it proves nothing");
        files.Should().Contain(f => f.RelativePath == EntitlementsFile, "the scan must see the Entitlements handlers, or it proves nothing");
        files.Single(f => f.RelativePath == LedgerFile).Text.Should().Contain("AuditAction.LedgerAccountCreate", "the scan's own premise");

        AuditActionFamilyRule.Scan(files).Should().BeEmpty();
    }

    [Theory]
    [InlineData("src/Modules/Entitlements/Decisya.Modules.Entitlements/Application/Rogue.cs", "AuditAction.LedgerAccountCreate")]
    [InlineData("src/Modules/Admin/Decisya.Modules.Admin/Endpoints/Rogue.cs", "AuditAction.LedgerAccountRename")]
    [InlineData("src/Decisya.Api/Program.cs", "AuditAction.LedgerAccountCurrencyChange")]
    [InlineData("src/Modules/Ledger/Decisya.Modules.Ledger/Application/Rogue.cs", "AuditAction.EntitlementsTrialStart")]
    [InlineData("src/Modules/Tenancy/Decisya.Modules.Tenancy/Application/Rogue.cs", "AuditAction.EntitlementsOverrideGrant")]
    [InlineData("src/Modules/Ledger/Decisya.Modules.Ledger/Application/Rogue.cs", "AuditAction . EntitlementsOverrideRevoke")]
    [InlineData("src/Modules/Entitlements/Decisya.Modules.Entitlements/Application/Rogue.cs", "Decisya.Modules.Audit.Contracts.AuditAction.LedgerAccountCreate")]
    public void A_red_fixture_that_appends_another_modules_action_fails_the_scan(string path, string reference)
    {
        var text = $"namespace X;\npublic static class Rogue {{ public static object A => {reference}; }}\n";

        var violations = AuditActionFamilyRule.Scan([(path, text)]);

        violations.Should().ContainSingle().Which.Should().StartWith(path);
    }

    [Theory]
    [InlineData("src/Modules/Ledger/Decisya.Modules.Ledger/Application/Rogue.cs")]
    [InlineData("src/Modules/Entitlements/Decisya.Modules.Entitlements/Application/Rogue.cs")]
    [InlineData("src/Decisya.Api/Program.cs")]
    public void A_using_static_of_AuditAction_outside_the_Audit_module_fails_the_scan(string path)
    {
        var violations = AuditActionFamilyRule.Scan([(path, "using static Decisya.Modules.Audit.Contracts.AuditAction;\nnamespace X;\n")]);

        violations.Should().ContainSingle().Which.Should().StartWith(path);
    }

    [Fact]
    public void Each_module_may_use_its_own_family_and_the_Audit_module_may_use_all_of_them()
    {
        var violations = AuditActionFamilyRule.Scan(
        [
            (LedgerFile, "var a = AuditAction.LedgerAccountCreate; var b = AuditAction.LedgerAccountRename;"),
            (EntitlementsFile, "var a = AuditAction.EntitlementsTrialStart;"),
            ("src/Modules/Audit/Decisya.Modules.Audit/Infrastructure/AuditActionConverter.cs", "AuditAction.LedgerAccountCreate; AuditAction.EntitlementsOverrideGrant;"),
            ("src/Modules/Audit/Decisya.Modules.Audit.Contracts/AuditAction.cs", "using static Decisya.Modules.Audit.Contracts.AuditAction;"),
        ]);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void A_whole_line_comment_is_not_a_use()
    {
        var violations = AuditActionFamilyRule.Scan([("src/Decisya.Api/Program.cs", "    // AuditAction.LedgerAccountCreate is the Ledger's.\nvar x = 1;")]);

        violations.Should().BeEmpty();
    }
}
