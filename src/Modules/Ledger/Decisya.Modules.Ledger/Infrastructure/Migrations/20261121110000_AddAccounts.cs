using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Decisya.Modules.Ledger.Infrastructure.Migrations;

/// <summary>
/// Issue #341. Creates schema <c>ledger</c> and tables <c>accounts</c> and <c>account_books</c>. Rollback note:
/// <see cref="Down"/> drops both tables and every account with them. Dump schema <c>ledger</c> first
/// (<c>pg_dump -n ledger</c>). The migrator never runs <see cref="Down"/>.
/// </summary>
public partial class AddAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "ledger");

        migrationBuilder.CreateTable(
            name: "account_books",
            schema: "ledger",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                lock_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_account_books", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "accounts",
            schema: "ledger",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                currency = table.Column<string>(type: "char(3)", nullable: false),
                opening_balance_minor = table.Column<long>(type: "bigint", nullable: false),
                opening_date = table.Column<DateOnly>(type: "date", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_accounts", x => x.id);
                table.CheckConstraint("ck_accounts_currency", "currency ~ '^[A-Z]{3}$'");
                table.CheckConstraint("ck_accounts_name", "char_length(name) BETWEEN 1 AND 80");
                table.CheckConstraint("ck_accounts_opening", "opening_date IS NOT NULL OR opening_balance_minor = 0");
            });

        migrationBuilder.CreateIndex(
            name: "ix_accounts_tenant_created",
            schema: "ledger",
            table: "accounts",
            columns: ["tenant_id", "created_at", "id"]);

        migrationBuilder.CreateIndex(
            name: "ux_account_books_tenant",
            schema: "ledger",
            table: "account_books",
            column: "tenant_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "account_books",
            schema: "ledger");

        migrationBuilder.DropTable(
            name: "accounts",
            schema: "ledger");
    }
}
