START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194513_AddRolesToSessionContext') THEN
    DROP FUNCTION identity_v1.session_context(uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194513_AddRolesToSessionContext') THEN
    CREATE FUNCTION identity_v1.session_context(p_session_id uuid)
    RETURNS TABLE (
      session_id uuid, user_id uuid, tenant_id int, company_id int, tenant_status text,
      user_active boolean, mfa_satisfied boolean, last_seen_at timestamptz,
      absolute_expiry timestamptz, revoked_at timestamptz, employee_id uuid,
      licensed_apps text[], idle_timeout_minutes int, roles text[], role_permissions text[])
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    AS $fn$
      SELECT s.id, u.id, u.tenant_id, u.company_id, t.status,
             (u.status = 'Active'), s.mfa_satisfied, s.last_seen_at,
             s.absolute_expiry, s.revoked_at, u.employee_id,
             coalesce((SELECT array_agg(ta.app ORDER BY ta.app)
                       FROM platform.tenant_app ta
                       WHERE ta.tenant_id = u.tenant_id AND ta.revoked_at IS NULL), '{}'),
             t.idle_timeout_minutes,
             u.roles,
             coalesce((SELECT array_agg(DISTINCT p ORDER BY p)
                       FROM identity.role r
                       CROSS JOIN LATERAL unnest(r.permissions) AS p
                       WHERE r.tenant_id = u.tenant_id AND r.public_id = ANY (u.roles)), '{}')
      FROM identity.session s
      JOIN identity."user" u ON u.id = s.user_id AND u.tenant_id = s.tenant_id
      JOIN platform.tenant t ON t.id = u.tenant_id
      WHERE s.id = p_session_id;
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194513_AddRolesToSessionContext') THEN
    REVOKE EXECUTE ON FUNCTION identity_v1.session_context(uuid) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194513_AddRolesToSessionContext') THEN
    GRANT EXECUTE ON FUNCTION identity_v1.session_context(uuid) TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20261016194513_AddRolesToSessionContext') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261016194513_AddRolesToSessionContext', '10.0.10');
    END IF;
END $EF$;
COMMIT;

