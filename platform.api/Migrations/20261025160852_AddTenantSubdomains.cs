using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSubdomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_subdomain",
                schema: "platform",
                columns: table => new
                {
                    subdomain = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replaced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_subdomain", x => x.subdomain);
                    table.ForeignKey(
                        name: "fk_tenant_subdomain_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "platform",
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_subdomain_tenant_id",
                schema: "platform",
                table: "tenant_subdomain",
                column: "tenant_id",
                unique: true,
                filter: "replaced_at IS NULL");

            // Every tenant made before addresses existed gets one, so none is left that nobody can
            // sign in to. Made from the public id ("ten_01h…" -> "t-01h…"): unique, already known to
            // the operator, and valid under TenantAddress's rules (a letter first, single hyphens).
            // The operator renames it to something readable from the tenant's page.
            migrationBuilder.Sql("""
                INSERT INTO platform.tenant_subdomain (subdomain, tenant_id, assigned_at)
                SELECT 't-' || lower(substr(public_id, 5)), id, now()
                FROM platform.tenant;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_subdomain",
                schema: "platform");
        }
    }
}
