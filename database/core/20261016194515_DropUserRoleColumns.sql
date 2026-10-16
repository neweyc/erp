START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194515_DropUserRoleColumns') THEN
    ALTER TABLE identity."user" DROP COLUMN role;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194515_DropUserRoleColumns') THEN
    ALTER TABLE identity."user" DROP COLUMN app_roles;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194515_DropUserRoleColumns') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261016194515_DropUserRoleColumns', '10.0.10');
    END IF;
END $EF$;
COMMIT;

