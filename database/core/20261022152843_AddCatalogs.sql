START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    ALTER TABLE core.employee ADD department_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    ALTER TABLE core.employee ADD job_title_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    ALTER TABLE core.employee ADD location_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE TABLE core.department (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        company_id integer NOT NULL,
        public_id character varying(34) NOT NULL,
        name character varying(100) NOT NULL,
        parent_id uuid,
        active boolean NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_department PRIMARY KEY (id),
        CONSTRAINT ak_departments_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT fk_department_company_tenant_id_company_id FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_department_department_tenant_id_parent_id FOREIGN KEY (tenant_id, parent_id) REFERENCES core.department (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE TABLE core.job_title (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        public_id character varying(34) NOT NULL,
        name character varying(100) NOT NULL,
        active boolean NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_job_title PRIMARY KEY (id),
        CONSTRAINT ak_job_titles_tenant_id_id UNIQUE (tenant_id, id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE TABLE core.location (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        company_id integer NOT NULL,
        public_id character varying(34) NOT NULL,
        name character varying(100) NOT NULL,
        active boolean NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_location PRIMARY KEY (id),
        CONSTRAINT ak_locations_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT fk_location_company_tenant_id_company_id FOREIGN KEY (tenant_id, company_id) REFERENCES core.company (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_employee_tenant_id_department_id ON core.employee (tenant_id, department_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_employee_tenant_id_job_title_id ON core.employee (tenant_id, job_title_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_employee_tenant_id_location_id ON core.employee (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE UNIQUE INDEX ix_department_public_id ON core.department (public_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_department_tenant_id ON core.department (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_department_tenant_id_company_id ON core.department (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_department_tenant_id_parent_id ON core.department (tenant_id, parent_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE UNIQUE INDEX ix_job_title_public_id ON core.job_title (public_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_job_title_tenant_id ON core.job_title (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE UNIQUE INDEX ix_location_public_id ON core.location (public_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_location_tenant_id ON core.location (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE INDEX ix_location_tenant_id_company_id ON core.location (tenant_id, company_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    ALTER TABLE core.employee ADD CONSTRAINT fk_employee_departments_tenant_id_department_id FOREIGN KEY (tenant_id, department_id) REFERENCES core.department (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    ALTER TABLE core.employee ADD CONSTRAINT fk_employee_job_titles_tenant_id_job_title_id FOREIGN KEY (tenant_id, job_title_id) REFERENCES core.job_title (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    ALTER TABLE core.employee ADD CONSTRAINT fk_employee_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES core.location (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE UNIQUE INDEX ix_department_tenant_id_parent_lower_name ON core.department (tenant_id, coalesce(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE UNIQUE INDEX ix_job_title_tenant_id_lower_name ON core.job_title (tenant_id, lower(name));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    CREATE UNIQUE INDEX ix_location_tenant_id_lower_name ON core.location (tenant_id, lower(name));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261022152843_AddCatalogs') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261022152843_AddCatalogs', '10.0.10');
    END IF;
END $EF$;
COMMIT;

