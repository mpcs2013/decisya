using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Decisya.Modules.Entitlements.Infrastructure.Migrations;

/// <summary>
/// Issue #23. Creates schema <c>entitlements</c> and tables <c>trial_grants</c>,
/// <c>feature_overrides</c>. Rollback note: <see cref="Down"/> drops both tables and loses
/// every trial and override. Every tenant then falls back to Free (fail-closed), and a
/// tenant whose trial was dropped could start a second trial.
/// </summary>
public partial class AddTrialGrantsAndFeatureOverrides : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "entitlements");

        migrationBuilder.CreateTable(
            name: "feature_overrides",
            schema: "entitlements",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                feature_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_feature_overrides", x => x.id);
                table.CheckConstraint("ck_feature_overrides_expiry", "expires_at IS NULL OR expires_at > granted_at");
            });

        migrationBuilder.CreateTable(
            name: "trial_grants",
            schema: "entitlements",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                plan = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_trial_grants", x => x.id);
                table.CheckConstraint("ck_trial_grants_period", "ends_at > starts_at");
            });

        migrationBuilder.CreateIndex(
            name: "ux_feature_overrides_tenant_feature",
            schema: "entitlements",
            table: "feature_overrides",
            columns: ["tenant_id", "feature_key"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_trial_grants_tenant",
            schema: "entitlements",
            table: "trial_grants",
            column: "tenant_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "feature_overrides",
            schema: "entitlements");

        migrationBuilder.DropTable(
            name: "trial_grants",
            schema: "entitlements");
    }
}
