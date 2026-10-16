using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <summary>
    /// Customer-defined roles, step 2 of 3: <c>identity_v1.session_context</c> returns <c>roles</c> and
    /// <c>role_permissions</c> in place of <c>role</c> and <c>app_roles</c> (docs/permissions.md §3).
    ///
    /// <c>role_permissions</c> is the union of the permissions of the user's CUSTOM roles, joined on the
    /// user's own tenant, so another tenant's role id in the array matches nothing. Built-in roles are
    /// resolved in code (packages/auth), so a release can change them without touching this function.
    ///
    /// Applied as <c>ap_owner</c> (database/README.md): the function is SECURITY DEFINER and runs with
    /// its owner's rights. DROP and CREATE, because PostgreSQL cannot change a function's RETURNS TABLE
    /// in place; dropping it also drops its grants, so they are restored in the same script.
    ///
    /// Still at most one row: every lookup is keyed by the session id, and the aggregates are scalar
    /// subqueries rather than joins, so nothing can multiply the row.
    /// </summary>
    public partial class AddRolesToSessionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION identity_v1.session_context(uuid);");
            migrationBuilder.Sql("""
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
                """);
            Grant(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Cycle 12's function. Step 3's Down has already restored the columns it reads.
            migrationBuilder.Sql("DROP FUNCTION identity_v1.session_context(uuid);");
            migrationBuilder.Sql("""
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
                """);
            Grant(migrationBuilder);
        }

        /// <summary>
        /// Functions are executable by PUBLIC when created, so the revoke comes first. The role list
        /// matches 02-grants.sql; a new customer service is granted there.
        /// </summary>
        private static void Grant(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE EXECUTE ON FUNCTION identity_v1.session_context(uuid) FROM PUBLIC;");
            migrationBuilder.Sql(
                "GRANT EXECUTE ON FUNCTION identity_v1.session_context(uuid) TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;");
        }
    }
}
