START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261121100000_AddAuditSubjectId') THEN
    ALTER TABLE audit.audit_records DROP CONSTRAINT ck_audit_records_feature_key;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261121100000_AddAuditSubjectId') THEN
    ALTER TABLE audit.audit_records DROP CONSTRAINT ck_audit_records_action;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261121100000_AddAuditSubjectId') THEN
    ALTER TABLE audit.audit_records ADD subject_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261121100000_AddAuditSubjectId') THEN
    ALTER TABLE audit.audit_records ADD CONSTRAINT ck_audit_records_action CHECK (action IN ('entitlements.trial.start', 'entitlements.override.grant', 'entitlements.override.revoke', 'ledger.account.create', 'ledger.account.rename', 'ledger.account.currency_change'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261121100000_AddAuditSubjectId') THEN
    ALTER TABLE audit.audit_records ADD CONSTRAINT ck_audit_records_feature_key CHECK ((action IN ('entitlements.override.grant', 'entitlements.override.revoke')) = (feature_key IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261121100000_AddAuditSubjectId') THEN
    ALTER TABLE audit.audit_records ADD CONSTRAINT ck_audit_records_subject CHECK ((action IN ('ledger.account.create', 'ledger.account.rename', 'ledger.account.currency_change')) = (subject_id IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20261121100000_AddAuditSubjectId') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261121100000_AddAuditSubjectId', '10.0.12');
    END IF;
END $EF$;
COMMIT;

