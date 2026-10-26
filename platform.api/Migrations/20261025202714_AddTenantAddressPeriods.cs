using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AppPlatform.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAddressPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_address_period",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    subdomain = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replaced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_address_period", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_address_period_tenant_subdomain_subdomain",
                        column: x => x.subdomain,
                        principalSchema: "platform",
                        principalTable: "tenant_subdomain",
                        principalColumn: "subdomain",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_address_period_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "platform",
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_address_period_subdomain",
                schema: "platform",
                table: "tenant_address_period",
                column: "subdomain");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_address_period_tenant_id",
                schema: "platform",
                table: "tenant_address_period",
                column: "tenant_id");

            // Former addresses recorded before this table existed: one span each, as their rows
            // still say. A span already overwritten by taking an address back cannot be recovered.
            migrationBuilder.Sql("""
                INSERT INTO platform.tenant_address_period (tenant_id, subdomain, assigned_at, replaced_at)
                SELECT tenant_id, subdomain, assigned_at, replaced_at
                FROM platform.tenant_subdomain
                WHERE replaced_at IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_address_period",
                schema: "platform");
        }
    }
}
