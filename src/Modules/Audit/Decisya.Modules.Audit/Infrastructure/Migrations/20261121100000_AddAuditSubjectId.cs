using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Decisya.Modules.Audit.Infrastructure.Migrations;

/// <summary>
/// Issue #341 (ADR-0013 amendment 1, point 7). Adds the nullable <c>subject_id uuid</c> column, replaces
/// <c>ck_audit_records_action</c> with the six action codes, replaces <c>ck_audit_records_feature_key</c> with
/// "a feature key is present exactly for the two override actions", and adds <c>ck_audit_records_subject</c>
/// ("a subject id is present exactly for the three ledger actions"). Existing rows satisfy all three.
/// Rollback note: <see cref="Down"/> is not supported once any ledger audit record exists. Roll forward.
/// Never delete or update audit records to make a rollback pass (ADR-0013). The migrator never runs <c>Down</c>.
/// </summary>
public partial class AddAuditSubjectId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_records_feature_key",
            schema: "audit",
            table: "audit_records");

        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_records_action",
            schema: "audit",
            table: "audit_records");

        migrationBuilder.AddColumn<Guid>(
            name: "subject_id",
            schema: "audit",
            table: "audit_records",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_records_action",
            schema: "audit",
            table: "audit_records",
            sql: "action IN ('entitlements.trial.start', 'entitlements.override.grant', 'entitlements.override.revoke', 'ledger.account.create', 'ledger.account.rename', 'ledger.account.currency_change')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_records_feature_key",
            schema: "audit",
            table: "audit_records",
            sql: "(action IN ('entitlements.override.grant', 'entitlements.override.revoke')) = (feature_key IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_records_subject",
            schema: "audit",
            table: "audit_records",
            sql: "(action IN ('ledger.account.create', 'ledger.account.rename', 'ledger.account.currency_change')) = (subject_id IS NOT NULL)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_records_subject",
            schema: "audit",
            table: "audit_records");

        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_records_feature_key",
            schema: "audit",
            table: "audit_records");

        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_records_action",
            schema: "audit",
            table: "audit_records");

        migrationBuilder.DropColumn(
            name: "subject_id",
            schema: "audit",
            table: "audit_records");

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_records_action",
            schema: "audit",
            table: "audit_records",
            sql: "action IN ('entitlements.trial.start', 'entitlements.override.grant', 'entitlements.override.revoke')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_records_feature_key",
            schema: "audit",
            table: "audit_records",
            sql: "(action = 'entitlements.trial.start') = (feature_key IS NULL)");
    }
}
