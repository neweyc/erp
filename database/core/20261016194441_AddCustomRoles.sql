START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194441_AddCustomRoles') THEN
    CREATE TABLE identity.role (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        public_id character varying(34) NOT NULL,
        name character varying(100) NOT NULL,
        description character varying(500) NOT NULL,
        permissions text[] NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_role PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194441_AddCustomRoles') THEN
    CREATE UNIQUE INDEX ix_role_public_id ON identity.role (public_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194441_AddCustomRoles') THEN
    CREATE INDEX ix_role_tenant_id ON identity.role (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194441_AddCustomRoles') THEN
    CREATE UNIQUE INDEX ix_role_tenant_id_lower_name ON identity.role (tenant_id, lower(name));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194441_AddCustomRoles') THEN
    ALTER TABLE identity."user" ADD roles text[] NOT NULL DEFAULT ARRAY[]::text[];
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194441_AddCustomRoles') THEN
    UPDATE identity."user" u
    SET roles = ARRAY['core:' || u.role] || coalesce(
      (SELECT array_agg(firsts.entry ORDER BY firsts.entry)
       FROM (SELECT DISTINCT ON (split_part(t.entry, ':', 1)) t.entry
             FROM unnest(u.app_roles) WITH ORDINALITY AS t(entry, ord)
             WHERE t.entry ~ '^[^:]+:.+$' AND split_part(t.entry, ':', 1) <> 'core'
             ORDER BY split_part(t.entry, ':', 1), t.ord) AS firsts),
      '{}');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194441_AddCustomRoles') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261016194441_AddCustomRoles', '10.0.10');
    END IF;
END $EF$;
COMMIT;

