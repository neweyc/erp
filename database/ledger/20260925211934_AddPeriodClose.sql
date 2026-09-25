START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger.__ef_migrations_history WHERE "migration_id" = '20260925211934_AddPeriodClose') THEN
    CREATE TABLE ledger.books (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        company_id integer NOT NULL,
        public_id character varying(34) NOT NULL,
        closed_through date,
        version bigint NOT NULL,
        CONSTRAINT pk_books PRIMARY KEY (id),
        CONSTRAINT ak_books_tenant_id_company_id UNIQUE (tenant_id, company_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger.__ef_migrations_history WHERE "migration_id" = '20260925211934_AddPeriodClose') THEN
    CREATE UNIQUE INDEX ix_books_public_id ON ledger.books (public_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger.__ef_migrations_history WHERE "migration_id" = '20260925211934_AddPeriodClose') THEN
    CREATE INDEX ix_books_tenant_id ON ledger.books (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger.__ef_migrations_history WHERE "migration_id" = '20260925211934_AddPeriodClose') THEN
    ALTER TABLE ledger.journal_entry ADD CONSTRAINT fk_journal_entry_books_tenant_id_company_id FOREIGN KEY (tenant_id, company_id) REFERENCES ledger.books (tenant_id, company_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger.__ef_migrations_history WHERE "migration_id" = '20260925211934_AddPeriodClose') THEN
    -- Closing only moves forward. There is no reopen: a closed period stays exactly what
    -- was reported for it, and a mistake found in it is corrected in an open one.
    CREATE FUNCTION ledger.assert_close_moves_forward() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog
    AS $$
    BEGIN
      IF (NEW.tenant_id, NEW.company_id) <> (OLD.tenant_id, OLD.company_id)
         OR (OLD.closed_through IS NOT NULL
             AND (NEW.closed_through IS NULL OR NEW.closed_through < OLD.closed_through)) THEN
        RAISE EXCEPTION 'books for company % are closed through %: a close cannot move back',
          OLD.company_id, OLD.closed_through
          USING ERRCODE = 'check_violation';
      END IF;

      RETURN NEW;
    END
    $$;

    CREATE TRIGGER books_close_moves_forward
      BEFORE UPDATE ON ledger.books
      FOR EACH ROW EXECUTE FUNCTION ledger.assert_close_moves_forward();

    -- No entry may be dated in a closed period.
    --
    -- FOR SHARE on the company's books row: a close is an UPDATE of that row, so a post and a
    -- close cannot interleave. While a post is in flight the close waits for it; while a close
    -- is in flight the post waits, then reads the new closing date and is refused. Without the
    -- lock, a post that checked just before a close committed would land inside the period
    -- the close had just shut. The foreign key guarantees the row exists.
    CREATE FUNCTION ledger.assert_entry_in_open_period() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog
    AS $$
    DECLARE
      closed date;
    BEGIN
      SELECT closed_through INTO closed FROM ledger.books
       WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
       FOR SHARE;

      IF closed IS NOT NULL AND NEW.entry_date <= closed THEN
        RAISE EXCEPTION 'the books are closed through %: an entry cannot be dated %', closed, NEW.entry_date
          USING ERRCODE = 'check_violation', CONSTRAINT = 'ledger_period_closed';
      END IF;

      RETURN NEW;
    END
    $$;

    CREATE TRIGGER journal_entry_in_open_period
      BEFORE INSERT ON ledger.journal_entry
      FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_in_open_period();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ledger.__ef_migrations_history WHERE "migration_id" = '20260925211934_AddPeriodClose') THEN
    INSERT INTO ledger.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260925211934_AddPeriodClose', '10.0.10');
    END IF;
END $EF$;
COMMIT;

