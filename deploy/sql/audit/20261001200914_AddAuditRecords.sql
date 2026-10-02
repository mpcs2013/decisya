DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'audit') THEN
        CREATE SCHEMA audit;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS audit."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261001200914_AddAuditRecords') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'audit') THEN
            CREATE SCHEMA audit;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261001200914_AddAuditRecords') THEN
    CREATE TABLE audit.audit_records (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        occurred_at timestamp with time zone NOT NULL,
        actor_user_id character varying(255) NOT NULL,
        action character varying(64) NOT NULL,
        outcome character varying(16) NOT NULL,
        feature_key character varying(64),
        trace_id character varying(32),
        CONSTRAINT "PK_audit_records" PRIMARY KEY (id),
        CONSTRAINT ck_audit_records_action CHECK (action IN ('entitlements.trial.start', 'entitlements.override.grant', 'entitlements.override.revoke')),
        CONSTRAINT ck_audit_records_actor CHECK (length(btrim(actor_user_id)) > 0),
        CONSTRAINT ck_audit_records_feature_key CHECK ((action = 'entitlements.trial.start') = (feature_key IS NULL)),
        CONSTRAINT ck_audit_records_outcome CHECK (outcome IN ('succeeded')),
        CONSTRAINT ck_audit_records_trace_id CHECK (trace_id IS NULL OR trace_id ~ '^[0-9a-f]{32}$')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261001200914_AddAuditRecords') THEN
    CREATE INDEX ix_audit_records_tenant_occurred ON audit.audit_records (tenant_id, occurred_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261001200914_AddAuditRecords') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261001200914_AddAuditRecords', '10.0.12');
    END IF;
END $EF$;
COMMIT;

