START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165914_AddAppRolesToSessionContext') THEN
    DROP FUNCTION identity_v1.session_context(uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165914_AddAppRolesToSessionContext') THEN
    CREATE FUNCTION identity_v1.session_context(p_session_id uuid)
    RETURNS TABLE (
      session_id uuid, user_id uuid, tenant_id int, company_id int, tenant_status text,
      role text, user_active boolean, mfa_satisfied boolean, last_seen_at timestamptz,
      absolute_expiry timestamptz, revoked_at timestamptz, employee_id uuid,
      licensed_apps text[], idle_timeout_minutes int, app_roles text[])
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    AS $fn$
      SELECT s.id, u.id, u.tenant_id, u.company_id, t.status,
             u.role, (u.status = 'Active'), s.mfa_satisfied, s.last_seen_at,
             s.absolute_expiry, s.revoked_at, u.employee_id,
             coalesce(array_agg(ta.app) FILTER (WHERE ta.app IS NOT NULL), '{}'),
             t.idle_timeout_minutes, u.app_roles
      FROM identity.session s
      JOIN identity."user" u ON u.id = s.user_id AND u.tenant_id = s.tenant_id
      JOIN platform.tenant t ON t.id = u.tenant_id
      LEFT JOIN platform.tenant_app ta
        ON ta.tenant_id = u.tenant_id AND ta.revoked_at IS NULL
      WHERE s.id = p_session_id
      GROUP BY s.id, u.id, u.tenant_id, u.company_id, t.status, u.role, u.status,
               s.mfa_satisfied, s.last_seen_at, s.absolute_expiry, s.revoked_at,
               u.employee_id, t.idle_timeout_minutes, u.app_roles;
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165914_AddAppRolesToSessionContext') THEN
    REVOKE EXECUTE ON FUNCTION identity_v1.session_context(uuid) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165914_AddAppRolesToSessionContext') THEN
    GRANT EXECUTE ON FUNCTION identity_v1.session_context(uuid) TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261015165914_AddAppRolesToSessionContext') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261015165914_AddAppRolesToSessionContext', '10.0.10');
    END IF;
END $EF$;
COMMIT;

