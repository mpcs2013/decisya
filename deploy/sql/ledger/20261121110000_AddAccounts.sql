DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'ledger') THEN
        CREATE SCHEMA ledger;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS ledger."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger."__EFMigrationsHistory" WHERE "MigrationId" = '20261121110000_AddAccounts') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'ledger') THEN
            CREATE SCHEMA ledger;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger."__EFMigrationsHistory" WHERE "MigrationId" = '20261121110000_AddAccounts') THEN
    CREATE TABLE ledger.account_books (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        lock_version bigint NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_account_books" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger."__EFMigrationsHistory" WHERE "MigrationId" = '20261121110000_AddAccounts') THEN
    CREATE TABLE ledger.accounts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name character varying(80) NOT NULL,
        currency char(3) NOT NULL,
        opening_balance_minor bigint NOT NULL,
        opening_date date,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_accounts" PRIMARY KEY (id),
        CONSTRAINT ck_accounts_currency CHECK (currency ~ '^[A-Z]{3}$'),
        CONSTRAINT ck_accounts_name CHECK (char_length(name) BETWEEN 1 AND 80),
        CONSTRAINT ck_accounts_opening CHECK (opening_date IS NOT NULL OR opening_balance_minor = 0)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger."__EFMigrationsHistory" WHERE "MigrationId" = '20261121110000_AddAccounts') THEN
    CREATE INDEX ix_accounts_tenant_created ON ledger.accounts (tenant_id, created_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger."__EFMigrationsHistory" WHERE "MigrationId" = '20261121110000_AddAccounts') THEN
    CREATE UNIQUE INDEX ux_account_books_tenant ON ledger.account_books (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger."__EFMigrationsHistory" WHERE "MigrationId" = '20261121110000_AddAccounts') THEN
    INSERT INTO ledger."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261121110000_AddAccounts', '10.0.15');
    END IF;
END $EF$;
COMMIT;

