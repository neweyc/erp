using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <summary>
    /// Adds <c>app_roles</c> to <c>identity_v1.session_context</c>, so every service can compute the
    /// caller's permissions (docs/permissions.md §3).
    ///
    /// Applied as <c>ap_owner</c> (database/README.md): the function is SECURITY DEFINER and runs with
    /// its owner's rights. It is a separate migration from AddUserAppRoles, so the column is added by
    /// the migration role as usual and only this step needs the owner.
    ///
    /// DROP and CREATE, not CREATE OR REPLACE, because PostgreSQL cannot change a function's
    /// RETURNS TABLE in place. Dropping it also drops its grants, so they are restored here, in the
    /// same transaction. Otherwise every customer API would answer 500 between this script and the
    /// next run of 02-grants.sql, which grants the same.
    /// </summary>
    public partial class AddAppRolesToSessionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION identity_v1.session_context(uuid);");
            migrationBuilder.Sql("""
                CREATE FUNCTION identity_v1.session_context(p_session_id uuid)
                RETURNS TABLE (
                  session_id uuid, user_id uuid, tenant_id int, company_id int, tenant_status text,
                  role text, user_active boolean, mfa_satisfied boolean, last_seen_at timestamptz,
                  absolute_expiry timestamptz, revoked_at timestamptz, employee_id uuid,
                  licensed_apps text[], idle_timeout_minutes int)
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $fn$
                  SELECT s.id, u.id, u.tenant_id, u.company_id, t.status,
                         u.role, (u.status = 'Active'), s.mfa_satisfied, s.last_seen_at,
                         s.absolute_expiry, s.revoked_at, u.employee_id,
                         coalesce(array_agg(ta.app) FILTER (WHERE ta.app IS NOT NULL), '{}'),
                         t.idle_timeout_minutes
                  FROM identity.session s
                  JOIN identity."user" u ON u.id = s.user_id AND u.tenant_id = s.tenant_id
                  JOIN platform.tenant t ON t.id = u.tenant_id
                  LEFT JOIN platform.tenant_app ta
                    ON ta.tenant_id = u.tenant_id AND ta.revoked_at IS NULL
                  WHERE s.id = p_session_id
                  GROUP BY s.id, u.id, u.tenant_id, u.company_id, t.status, u.role, u.status,
                           s.mfa_satisfied, s.last_seen_at, s.absolute_expiry, s.revoked_at,
                           u.employee_id, t.idle_timeout_minutes;
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
