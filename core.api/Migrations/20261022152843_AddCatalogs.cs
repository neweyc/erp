using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <summary>
    /// Departments, job titles and locations, and the employee's placement in each. Applied by the
    /// migration role; the published views over them are AddCatalogViews, applied as ap_owner.
    ///
    /// Hand-edited: EF cannot express the case-insensitive name indexes, added in SQL at the end.
    /// </summary>
    public partial class AddCatalogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                schema: "core",
                table: "employee",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "job_title_id",
                schema: "core",
                table: "employee",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                schema: "core",
                table: "employee",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "department",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_department", x => x.id);
                    table.UniqueConstraint("ak_departments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_department_company_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalSchema: "core",
                        principalTable: "company",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_department_department_tenant_id_parent_id",
                        columns: x => new { x.tenant_id, x.parent_id },
                        principalSchema: "core",
                        principalTable: "department",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_title",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_title", x => x.id);
                    table.UniqueConstraint("ak_job_titles_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "location",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_location", x => x.id);
                    table.UniqueConstraint("ak_locations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_location_company_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalSchema: "core",
                        principalTable: "company",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_id_department_id",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_id_job_title_id",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "job_title_id" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_id_location_id",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_department_public_id",
                schema: "core",
                table: "department",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_department_tenant_id",
                schema: "core",
                table: "department",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_department_tenant_id_company_id",
                schema: "core",
                table: "department",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_department_tenant_id_parent_id",
                schema: "core",
                table: "department",
                columns: new[] { "tenant_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_job_title_public_id",
                schema: "core",
                table: "job_title",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_title_tenant_id",
                schema: "core",
                table: "job_title",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_location_public_id",
                schema: "core",
                table: "location",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_location_tenant_id",
                schema: "core",
                table: "location",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_location_tenant_id_company_id",
                schema: "core",
                table: "location",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_employee_departments_tenant_id_department_id",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "department_id" },
                principalSchema: "core",
                principalTable: "department",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_employee_job_titles_tenant_id_job_title_id",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "job_title_id" },
                principalSchema: "core",
                principalTable: "job_title",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_employee_locations_tenant_id_location_id",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "location_id" },
                principalSchema: "core",
                principalTable: "location",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Names unique IGNORING CASE, retired rows included: "Sales" and "sales" would be
            // indistinguishable in a picker, and reactivating is the way back to a retired name.
            // A department's name is unique among its siblings only, so "Support" can sit under both
            // Sales and Engineering; coalesce makes the top level one group, since NULLs never clash.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_department_tenant_id_parent_lower_name ON core.department " +
                "(tenant_id, coalesce(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_job_title_tenant_id_lower_name ON core.job_title (tenant_id, lower(name));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_location_tenant_id_lower_name ON core.location (tenant_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_employee_departments_tenant_id_department_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropForeignKey(
                name: "fk_employee_job_titles_tenant_id_job_title_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropForeignKey(
                name: "fk_employee_locations_tenant_id_location_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropTable(
                name: "department",
                schema: "core");

            migrationBuilder.DropTable(
                name: "job_title",
                schema: "core");

            migrationBuilder.DropTable(
                name: "location",
                schema: "core");

            migrationBuilder.DropIndex(
                name: "ix_employee_tenant_id_department_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropIndex(
                name: "ix_employee_tenant_id_job_title_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropIndex(
                name: "ix_employee_tenant_id_location_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropColumn(
                name: "department_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropColumn(
                name: "job_title_id",
                schema: "core",
                table: "employee");

            migrationBuilder.DropColumn(
                name: "location_id",
                schema: "core",
                table: "employee");
        }
    }
}
