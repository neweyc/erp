using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <summary>
    /// Customer-defined roles, step 3 of 3: drops Cycle 12's <c>identity.user.role</c> and
    /// <c>app_roles</c>, which <c>roles</c> replaced in step 1. Applied by the migration role, AFTER
    /// step 2, because until then <c>session_context</c> still reads them.
    ///
    /// Destructive in the same release as the code that stops using the columns. Acceptable only
    /// because nothing is deployed and the one supported combination is "everything from the same
    /// commit" (docs/compatibility.md). From the first customer deployment this would wait a release.
    /// </summary>
    public partial class DropUserRoleColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "role", schema: "identity", table: "user");
            migrationBuilder.DropColumn(name: "app_roles", schema: "identity", table: "user");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "role", schema: "identity", table: "user",
                type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "member");
            migrationBuilder.AddColumn<string[]>(
                name: "app_roles", schema: "identity", table: "user",
                type: "text[]", nullable: false, defaultValue: new string[0]);

            // Best effort back to Cycle 12's shape, which had one core role and one role per app.
            // Custom roles have no equivalent there and are lost from the old columns.
            migrationBuilder.Sql("""
                UPDATE identity."user" u
                SET role = coalesce(
                      (SELECT substr(k, 6) FROM unnest(u.roles) AS k WHERE k LIKE 'core:%' ORDER BY k LIMIT 1),
                      'member'),
                    app_roles = coalesce(
                      (SELECT array_agg(k ORDER BY k)
                       FROM unnest(u.roles) AS k WHERE k LIKE '%:%' AND k NOT LIKE 'core:%'),
                      '{}');
                """);
        }
    }
}
