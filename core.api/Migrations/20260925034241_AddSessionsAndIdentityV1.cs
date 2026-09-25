using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionsAndIdentityV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_user_tenant_id_id",
                schema: "identity",
                table: "user",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "session",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mfa_satisfied = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expiry = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_user_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalSchema: "identity",
                        principalTable: "user",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_tenant_id",
                schema: "identity",
                table: "session",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_tenant_id_user_id",
                schema: "identity",
                table: "session",
                columns: new[] { "tenant_id", "user_id" });

        // The published session contract. A FUNCTION, not a view: a grant on a view is a grant
        // to read ALL of it, and a session id is credential-equivalent — SELECT * would
        // enumerate every live session in every tenant.
        //
        // Created as ap_owner (see AddCoreV1Views): it reads across identity AND platform, and
        // core's migration role can see neither of those together.
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS identity_v1;");

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION identity_v1.session_context(p_session_id uuid)
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

        // Touch is a function too, so no API role needs UPDATE on the session table itself.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION identity_v1.touch_session(p_session_id uuid)
            RETURNS void
            LANGUAGE sql
            SECURITY DEFINER
            SET search_path = pg_catalog, pg_temp
            AS $fn$
              UPDATE identity.session SET last_seen_at = now() WHERE id = p_session_id;
            $fn$;
            """);

        // Granted in the same script as the object. EXECUTE is revoked from PUBLIC first:
        // functions are executable by PUBLIC on creation, so skipping the revoke grants them to
        // every role in the cluster.
        migrationBuilder.Sql("REVOKE EXECUTE ON FUNCTION identity_v1.session_context(uuid) FROM PUBLIC;");
        migrationBuilder.Sql("REVOKE EXECUTE ON FUNCTION identity_v1.touch_session(uuid) FROM PUBLIC;");
        migrationBuilder.Sql("GRANT USAGE ON SCHEMA identity_v1 TO ap_core_rt;");
        migrationBuilder.Sql("GRANT EXECUTE ON FUNCTION identity_v1.session_context(uuid) TO ap_core_rt;");
        migrationBuilder.Sql("GRANT EXECUTE ON FUNCTION identity_v1.touch_session(uuid) TO ap_core_rt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity_v1.touch_session(uuid);");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity_v1.session_context(uuid);");

            migrationBuilder.DropTable(
                name: "session",
                schema: "identity");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_user_tenant_id_id",
                schema: "identity",
                table: "user");
        }
    }
}
