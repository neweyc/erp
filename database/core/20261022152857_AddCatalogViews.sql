START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152857_AddCatalogViews') THEN
    CREATE OR REPLACE VIEW core_v1.department AS
    SELECT id, tenant_id, company_id, public_id, name, parent_id, active
    FROM core.department;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152857_AddCatalogViews') THEN
    CREATE OR REPLACE VIEW core_v1.job_title AS
    SELECT id, tenant_id, public_id, name, active
    FROM core.job_title;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152857_AddCatalogViews') THEN
    CREATE OR REPLACE VIEW core_v1.location AS
    SELECT id, tenant_id, company_id, public_id, name, active
    FROM core.location;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152857_AddCatalogViews') THEN
    CREATE OR REPLACE VIEW core_v1.employee AS
    SELECT e.id, e.tenant_id, e.company_id, e.public_id,
           e.first_name, e.last_name,
           (e.first_name || ' ' || e.last_name) AS display_name,
           e.status,
           e.department_id, d.name AS department_name,
           e.job_title_id, j.name AS job_title_name,
           e.location_id, l.name AS location_name
    FROM core.employee e
    LEFT JOIN core.department d ON d.tenant_id = e.tenant_id AND d.id = e.department_id
    LEFT JOIN core.job_title j ON j.tenant_id = e.tenant_id AND j.id = e.job_title_id
    LEFT JOIN core.location l ON l.tenant_id = e.tenant_id AND l.id = e.location_id
    WHERE e.deleted = false;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152857_AddCatalogViews') THEN
    GRANT SELECT ON core_v1.department, core_v1.job_title, core_v1.location TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152857_AddCatalogViews') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261022152857_AddCatalogViews', '10.0.10');
    END IF;
END $EF$;
COMMIT;

