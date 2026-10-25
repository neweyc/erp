START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20261025160852_AddTenantSubdomains') THEN
    CREATE TABLE platform.tenant_subdomain (
        subdomain character varying(40) NOT NULL,
        tenant_id integer NOT NULL,
        assigned_at timestamp with time zone NOT NULL,
        replaced_at timestamp with time zone,
        CONSTRAINT pk_tenant_subdomain PRIMARY KEY (subdomain),
        CONSTRAINT fk_tenant_subdomain_tenant_tenant_id FOREIGN KEY (tenant_id) REFERENCES platform.tenant (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20261025160852_AddTenantSubdomains') THEN
    CREATE UNIQUE INDEX ix_tenant_subdomain_tenant_id ON platform.tenant_subdomain (tenant_id) WHERE replaced_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20261025160852_AddTenantSubdomains') THEN
    INSERT INTO platform.tenant_subdomain (subdomain, tenant_id, assigned_at)
    SELECT 't-' || lower(substr(public_id, 5)), id, now()
    FROM platform.tenant;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20261025160852_AddTenantSubdomains') THEN
    INSERT INTO platform.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261025160852_AddTenantSubdomains', '10.0.10');
    END IF;
END $EF$;
COMMIT;

