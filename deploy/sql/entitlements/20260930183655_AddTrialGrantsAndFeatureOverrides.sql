DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'entitlements') THEN
        CREATE SCHEMA entitlements;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS entitlements."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM entitlements."__EFMigrationsHistory" WHERE "MigrationId" = '20260930183655_AddTrialGrantsAndFeatureOverrides') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'entitlements') THEN
            CREATE SCHEMA entitlements;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM entitlements."__EFMigrationsHistory" WHERE "MigrationId" = '20260930183655_AddTrialGrantsAndFeatureOverrides') THEN
    CREATE TABLE entitlements.feature_overrides (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        feature_key character varying(64) NOT NULL,
        reason character varying(500) NOT NULL,
        granted_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone,
        CONSTRAINT "PK_feature_overrides" PRIMARY KEY (id),
        CONSTRAINT ck_feature_overrides_expiry CHECK (expires_at IS NULL OR expires_at > granted_at)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM entitlements."__EFMigrationsHistory" WHERE "MigrationId" = '20260930183655_AddTrialGrantsAndFeatureOverrides') THEN
    CREATE TABLE entitlements.trial_grants (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        plan character varying(16) NOT NULL,
        starts_at timestamp with time zone NOT NULL,
        ends_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_trial_grants" PRIMARY KEY (id),
        CONSTRAINT ck_trial_grants_period CHECK (ends_at > starts_at)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM entitlements."__EFMigrationsHistory" WHERE "MigrationId" = '20260930183655_AddTrialGrantsAndFeatureOverrides') THEN
    CREATE UNIQUE INDEX ux_feature_overrides_tenant_feature ON entitlements.feature_overrides (tenant_id, feature_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM entitlements."__EFMigrationsHistory" WHERE "MigrationId" = '20260930183655_AddTrialGrantsAndFeatureOverrides') THEN
    CREATE UNIQUE INDEX ux_trial_grants_tenant ON entitlements.trial_grants (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM entitlements."__EFMigrationsHistory" WHERE "MigrationId" = '20260930183655_AddTrialGrantsAndFeatureOverrides') THEN
    INSERT INTO entitlements."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260930183655_AddTrialGrantsAndFeatureOverrides', '10.0.12');
    END IF;
END $EF$;
COMMIT;

