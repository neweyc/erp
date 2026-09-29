using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AppPlatform.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "error_occurrence",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    app = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: true),
                    count = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_error_occurrence", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_error_occurrence_occurred_at",
                schema: "platform",
                table: "error_occurrence",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_error_occurrence_reference",
                schema: "platform",
                table: "error_occurrence",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_error_occurrence_tenant_id",
                schema: "platform",
                table: "error_occurrence",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "error_occurrence",
                schema: "platform");
        }
    }
}
