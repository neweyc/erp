START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929182954_AddDataKeysAndOperatorTotp') THEN
    ALTER TABLE platform.platform_user RENAME COLUMN totp_secret TO totp_secret_encrypted;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929182954_AddDataKeysAndOperatorTotp') THEN
    ALTER TABLE platform.platform_user ADD totp_last_used_step bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929182954_AddDataKeysAndOperatorTotp') THEN
    UPDATE platform.platform_session SET revoked_at = now() WHERE revoked_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929182954_AddDataKeysAndOperatorTotp') THEN
    CREATE TABLE platform.data_key (
        id uuid NOT NULL,
        kek_fingerprint character varying(16) NOT NULL,
        wrapped_key bytea NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_data_key PRIMARY KEY (id, kek_fingerprint)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929182954_AddDataKeysAndOperatorTotp') THEN
    INSERT INTO platform.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260929182954_AddDataKeysAndOperatorTotp', '10.0.10');
    END IF;
END $EF$;
COMMIT;

