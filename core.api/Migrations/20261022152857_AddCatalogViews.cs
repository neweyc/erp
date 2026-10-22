using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <summary>
    /// Publishes the catalogs to apps: <c>core_v1.department</c>, <c>core_v1.job_title</c> and
    /// <c>core_v1.location</c>, and adds each employee's placement to <c>core_v1.employee</c>.
    ///
    /// <b>Applied as ap_owner</b>, like AddCoreV1Views: a view runs with its owner's rights, which is
    /// how an app reads these while holding no grant on the core tables (database/README.md).
    ///
    /// Additive only. The employee view keeps its columns in their order and gains new ones at the
    /// end, which is all CREATE OR REPLACE VIEW allows and all a published contract may do.
    /// </summary>
    public partial class AddCatalogViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No department path here. Building it needs a recursive query over every tenant's
            // departments, which the planner cannot narrow to the reader's tenant; an app that
            // wants "Engineering › Platform" walks parent_id over its own tenant's rows instead.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW core_v1.department AS
                SELECT id, tenant_id, company_id, public_id, name, parent_id, active
                FROM core.department;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW core_v1.job_title AS
                SELECT id, tenant_id, public_id, name, active
                FROM core.job_title;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW core_v1.location AS
                SELECT id, tenant_id, company_id, public_id, name, active
                FROM core.location;
                """);

            // The names come along so an app can show where someone sits without a second view.
            // Joined on the tenant as well as the id, matching the composite foreign keys.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW core_v1.employee AS
                SELECT e.id, e.tenant_id, e.company_id, e.public_id,
                       e.first_name, e.last_name,
                       (e.first_name || ' ' || e.last_name) AS display_name,
                       e.status,
                       e.department_id, d.name AS department_name,
                       e.job_title_id, j.name AS job_title_name,
                       e.location_id, l.name AS location_name
                FROM core.employee e
                LEFT JOIN core.department d ON d.tenant_id = e.tenant_id AND d.id = e.department_id
                LEFT JOIN core.job_title j ON j.tenant_id = e.tenant_id AND j.id = e.job_title_id
                LEFT JOIN core.location l ON l.tenant_id = e.tenant_id AND l.id = e.location_id
                WHERE e.deleted = false;
                """);

            // Granted in the same script as the objects, to every service that reads core_v1 (as
            // 02-grants.sql does for the schema). A published view nobody can select from is a
            // broken deploy that every unit test passes.
            migrationBuilder.Sql(
                "GRANT SELECT ON core_v1.department, core_v1.job_title, core_v1.location " +
                "TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS core_v1.location;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS core_v1.job_title;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS core_v1.department;");

            // A view cannot lose columns by CREATE OR REPLACE, so the old shape is recreated and
            // its grants restored.
            migrationBuilder.Sql("DROP VIEW IF EXISTS core_v1.employee;");
            migrationBuilder.Sql("""
                CREATE VIEW core_v1.employee AS
                SELECT id, tenant_id, company_id, public_id,
                       first_name, last_name,
                       (first_name || ' ' || last_name) AS display_name,
                       status
                FROM core.employee
                WHERE deleted = false;
                """);
            migrationBuilder.Sql(
                "GRANT SELECT ON core_v1.employee TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;");
        }
    }
}
