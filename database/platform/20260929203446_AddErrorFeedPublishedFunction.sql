START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929203446_AddErrorFeedPublishedFunction') THEN
    CREATE SCHEMA IF NOT EXISTS platform_v1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929203446_AddErrorFeedPublishedFunction') THEN
    CREATE OR REPLACE FUNCTION platform_v1.record_error(
      p_reference text, p_fingerprint text, p_app text, p_tenant_id int, p_count int,
      p_occurred_at timestamptz)
    RETURNS void
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    AS $fn$
    BEGIN
      -- Every column allowlisted by shape. Anything else is refused rather than stored:
      -- this table is metadata only, and free text is where customer data would get in.
      IF p_reference IS NULL OR p_reference !~ '^err_[0-9a-z]{25}$'
         OR p_fingerprint IS NULL OR p_fingerprint !~ '^[0-9a-f]{16}$'
         OR p_app IS NULL OR p_app !~ '^[a-z][a-z0-9_]{1,19}$'
         OR p_count IS NULL OR p_count NOT BETWEEN 1 AND 100000
         OR p_occurred_at IS NULL
         OR p_occurred_at NOT BETWEEN now() - interval '1 day' AND now() + interval '5 minutes'
      THEN
        RAISE EXCEPTION 'platform_v1.record_error refused a malformed occurrence'
          USING ERRCODE = 'invalid_parameter_value';
      END IF;

      INSERT INTO platform.error_occurrence (reference, fingerprint, app, tenant_id, count, occurred_at)
      VALUES (p_reference, p_fingerprint, p_app, p_tenant_id, p_count, p_occurred_at);
    END
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929203446_AddErrorFeedPublishedFunction') THEN
    REVOKE EXECUTE ON FUNCTION platform_v1.record_error(text, text, text, int, int, timestamptz) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929203446_AddErrorFeedPublishedFunction') THEN
    GRANT USAGE ON SCHEMA platform_v1 TO ap_platform_rt, ap_core_rt, ap_tickets_rt, ap_ledger_rt;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929203446_AddErrorFeedPublishedFunction') THEN
    GRANT EXECUTE ON FUNCTION platform_v1.record_error(text, text, text, int, int, timestamptz) TO ap_platform_rt, ap_core_rt, ap_tickets_rt, ap_ledger_rt;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM platform.__ef_migrations_history WHERE "migration_id" = '20260929203446_AddErrorFeedPublishedFunction') THEN
    INSERT INTO platform.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260929203446_AddErrorFeedPublishedFunction', '10.0.10');
    END IF;
END $EF$;
COMMIT;

