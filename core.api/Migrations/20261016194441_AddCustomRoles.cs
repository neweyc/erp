using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <summary>
    /// Customer-defined roles (docs/permissions.md §3), step 1 of 3. Applied by the migration role.
    ///
    /// Adds <c>identity.role</c> and <c>identity.user.roles</c>, and fills <c>roles</c> from Cycle 12's
    /// <c>role</c> and <c>app_roles</c>: <c>admin</c> becomes <c>core:admin</c>, and app entries such as
    /// <c>tickets:agent</c> are already in the key format, so they are carried over as they are. The old columns stay until step 3, because the live
    /// <c>session_context</c> still reads them until step 2 (applied as ap_owner) replaces it.
    /// Dropping them here would break every customer API between the two scripts: PostgreSQL does
    /// not track a SQL function's dependencies on columns, so nothing would stop the drop.
    ///
    /// Hand-edited: EF scaffolded a drop and a rename, and cannot express the case-insensitive name index.
    /// </summary>
    public partial class AddCustomRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    permissions = table.Column<string[]>(type: "text[]", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_role_public_id",
                schema: "identity",
                table: "role",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_tenant_id",
                schema: "identity",
                table: "role",
                column: "tenant_id");

            // Unique per tenant IGNORING CASE. EF cannot model an index on an expression.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_role_tenant_id_lower_name ON identity.role (tenant_id, lower(name));");

            migrationBuilder.AddColumn<string[]>(
                name: "roles",
                schema: "identity",
                table: "user",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            // Carries over exactly what Cycle 12 honoured (AppRoles.Decode), and nothing it ignored:
            //  - malformed entries (no colon, or nothing either side of the FIRST colon) and NULLs granted
            //    nothing. Later colons belong to the role name, as Decode split them: such an entry
            //    still counts as that app's first entry, and carried over it still grants nothing;
            //  - a "core:" entry in app_roles was ignored, because only the role column set the core
            //    role. Copied as a key it would now be effective: a member could become an admin;
            //  - when an app appeared twice, only the FIRST entry counted.
            migrationBuilder.Sql("""
                UPDATE identity."user" u
                SET roles = ARRAY['core:' || u.role] || coalesce(
                  (SELECT array_agg(firsts.entry ORDER BY firsts.entry)
                   FROM (SELECT DISTINCT ON (split_part(t.entry, ':', 1)) t.entry
                         FROM unnest(u.app_roles) WITH ORDINALITY AS t(entry, ord)
                         WHERE t.entry ~ '^[^:]+:.+$' AND split_part(t.entry, ':', 1) <> 'core'
                         ORDER BY split_part(t.entry, ':', 1), t.ord) AS firsts),
                  '{}');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "roles", schema: "identity", table: "user");
            migrationBuilder.DropTable(name: "role", schema: "identity");
        }
    }
}
