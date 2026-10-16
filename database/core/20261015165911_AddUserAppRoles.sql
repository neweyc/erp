START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165911_AddUserAppRoles') THEN
    ALTER TABLE identity."user" ADD access_version bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165911_AddUserAppRoles') THEN
    ALTER TABLE identity."user" ADD app_roles text[] NOT NULL DEFAULT ARRAY[]::text[];
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165911_AddUserAppRoles') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261015165911_AddUserAppRoles', '10.0.10');
    END IF;
END $EF$;
COMMIT;

