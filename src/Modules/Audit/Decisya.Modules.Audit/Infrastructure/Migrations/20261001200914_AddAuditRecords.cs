using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Decisya.Modules.Audit.Infrastructure.Migrations;

/// <summary>
/// Issue #24. Creates schema <c>audit</c> and table <c>audit_records</c> with its five check
/// constraints and the tenant-first index. Rollback note: <see cref="Down"/> drops
/// <c>audit.audit_records</c> and EVERY audit record with it, and no application role could
/// do that (ADR-0013). Dump the schema first (<c>pg_dump -n audit</c>). The migrator never runs <c>Down</c>.
/// </summary>
public partial class AddAuditRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "audit");

        migrationBuilder.CreateTable(
            name: "audit_records",
            schema: "audit",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                actor_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                feature_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_records", x => x.id);
                table.CheckConstraint("ck_audit_records_action", "action IN ('entitlements.trial.start', 'entitlements.override.grant', 'entitlements.override.revoke')");
                table.CheckConstraint("ck_audit_records_actor", "length(btrim(actor_user_id)) > 0");
                table.CheckConstraint("ck_audit_records_feature_key", "(action = 'entitlements.trial.start') = (feature_key IS NULL)");
                table.CheckConstraint("ck_audit_records_outcome", "outcome IN ('succeeded')");
                table.CheckConstraint("ck_audit_records_trace_id", "trace_id IS NULL OR trace_id ~ '^[0-9a-f]{32}$'");
            });

        migrationBuilder.CreateIndex(
            name: "ix_audit_records_tenant_occurred",
            schema: "audit",
            table: "audit_records",
            columns: ["tenant_id", "occurred_at"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_records",
            schema: "audit");
    }
}
