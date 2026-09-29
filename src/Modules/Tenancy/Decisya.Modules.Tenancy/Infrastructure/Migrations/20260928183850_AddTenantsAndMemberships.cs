using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Decisya.Modules.Tenancy.Infrastructure.Migrations;

/// <summary>
/// Issue #21. Creates schema <c>tenancy</c> and tables <c>tenants</c>, <c>memberships</c>.
/// Rollback note: <see cref="Down"/> drops both tables and loses every tenant and
/// membership row ever recorded. The next sign-in re-provisions a caller's own tenant and
/// Owner membership through JIT provisioning (Story 1), but the original <c>created_at</c>
/// is not recoverable.
/// </summary>
public partial class AddTenantsAndMemberships : Migration
{
    private static readonly string[] MembershipTenantUserIndexColumns = ["tenant_id", "user_id"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "tenancy");

        migrationBuilder.CreateTable(
            name: "tenants",
            schema: "tenancy",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenants", x => x.tenant_id);
            });

        migrationBuilder.CreateTable(
            name: "memberships",
            schema: "tenancy",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_memberships", x => x.id);
                table.ForeignKey(
                    name: "FK_memberships_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalSchema: "tenancy",
                    principalTable: "tenants",
                    principalColumn: "tenant_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_memberships_tenant_user",
            schema: "tenancy",
            table: "memberships",
            columns: MembershipTenantUserIndexColumns,
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "memberships",
            schema: "tenancy");

        migrationBuilder.DropTable(
            name: "tenants",
            schema: "tenancy");
    }
}
