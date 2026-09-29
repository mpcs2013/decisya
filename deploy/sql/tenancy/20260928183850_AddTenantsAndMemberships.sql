DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'tenancy') THEN
        CREATE SCHEMA tenancy;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS tenancy."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260928183850_AddTenantsAndMemberships') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'tenancy') THEN
            CREATE SCHEMA tenancy;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260928183850_AddTenantsAndMemberships') THEN
    CREATE TABLE tenancy.tenants (
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_tenants" PRIMARY KEY (tenant_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260928183850_AddTenantsAndMemberships') THEN
    CREATE TABLE tenancy.memberships (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        user_id character varying(255) NOT NULL,
        role character varying(16) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_memberships" PRIMARY KEY (id),
        CONSTRAINT "FK_memberships_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenancy.tenants (tenant_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260928183850_AddTenantsAndMemberships') THEN
    CREATE UNIQUE INDEX ux_memberships_tenant_user ON tenancy.memberships (tenant_id, user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260928183850_AddTenantsAndMemberships') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260928183850_AddTenantsAndMemberships', '10.0.12');
    END IF;
END $EF$;
COMMIT;

