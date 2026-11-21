using Decisya.Infrastructure.Persistence;
using Decisya.Infrastructure.Persistence.Conversions;
using Decisya.Modules.Ledger.Domain;
using Decisya.SharedKernel;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Decisya.Modules.Ledger.Infrastructure;

/// <summary>
/// The Ledger module's <see cref="TenantDbContext"/> (issue #341, module-scaffold step 6; G2). Schema
/// <c>ledger</c>: tables <c>accounts</c> and <c>account_books</c>. Public only because the migrator builds it;
/// the <c>DbSet</c>s and the entities are internal.
/// </summary>
public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    /// <summary>The module's schema name (also <c>LedgerModule.Schema</c>).</summary>
    internal const string Schema = LedgerModule.Schema;

    internal DbSet<Account> Accounts => Set<Account>();

    internal DbSet<AccountBook> AccountBooks => Set<AccountBook>();

    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Account>(account =>
        {
            account.ToTable("accounts", table =>
            {
                table.HasCheckConstraint("ck_accounts_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_accounts_name", $"char_length(name) BETWEEN 1 AND {AccountName.MaxLength}");
                table.HasCheckConstraint("ck_accounts_opening", "opening_date IS NOT NULL OR opening_balance_minor = 0");
            });

            account.HasKey(a => a.Id);
            account.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            account.Property(a => a.TenantId).HasColumnName("tenant_id");
            account.Property(a => a.Name).HasColumnName("name").HasMaxLength(AccountName.MaxLength).IsRequired();
            account.Property(a => a.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
            account.Property(a => a.OpeningBalanceMinor).HasColumnName("opening_balance_minor").IsRequired();
            account.Property(a => a.OpeningDate).HasColumnName("opening_date");
            account.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
            account.Ignore(a => a.OpeningBalance);

            account.HasIndex(a => new { a.TenantId, a.CreatedAt, a.Id }).HasDatabaseName("ix_accounts_tenant_created");
        });

        modelBuilder.Entity<AccountBook>(book =>
        {
            book.ToTable("account_books");
            book.HasKey(b => b.Id);
            book.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
            book.Property(b => b.TenantId).HasColumnName("tenant_id");
            book.Property(b => b.LockVersion).HasColumnName("lock_version").IsRequired();
            book.Property(b => b.CreatedAt).HasColumnName("created_at").IsRequired();

            book.HasIndex(b => b.TenantId).IsUnique().HasDatabaseName("ux_account_books_tenant");
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<Instant>().HaveConversion<InstantConverter>();
        configurationBuilder.Properties<LocalDate>().HaveConversion<LocalDateConverter>();
        configurationBuilder.Properties<Currency>().HaveConversion<CurrencyConverter>();
    }
}
