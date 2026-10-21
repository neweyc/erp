START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261017152346_AddUserTokenMessageId') THEN
    ALTER TABLE identity.user_token ADD message_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261017152346_AddUserTokenMessageId') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261017152346_AddUserTokenMessageId', '10.0.10');
    END IF;
END $EF$;
COMMIT;

