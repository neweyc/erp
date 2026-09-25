using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <summary>
    /// The published contract other services read.
///
    /// **Apply this migration as <c>ap_owner</c>, not as <c>ap_core_migrate</c>.** A view executes
    /// with its OWNER's privileges, and that is the entire mechanism by which <c>ap_tickets_rt</c>
    /// reads <c>core_v1.employee</c> while holding no grant whatsoever on <c>core.employee</c>.
    /// Created by the migration role instead, every consumer read fails naming a table the
    /// consumer is deliberately not supposed to reach.
///
    /// <c>ap_core_migrate</c> cannot <c>SET ROLE ap_owner</c> — membership runs the other way, so
    /// that ap_owner inherits read on what the migration roles create. Applying this one as
    /// ap_owner is therefore a separate, deliberate step in the runbook.
    /// </summary>
    public partial class AddCoreV1Views : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS core_v1;");

        // Deliberately absent: anything encrypted, and any PII beyond a display name.
        // Ciphertext cannot be filtered or sorted in SQL, and a consuming app has no business
        // holding it. tenant_id IS present, so a consumer applies its ordinary ITenantScoped
        // filter to this view with no new machinery.
        //
        // security_invoker is left OFF (the default) and must stay off: setting it resolves
        // permissions as the CALLER, which collapses the boundary this view exists to create.
        migrationBuilder.Sql("""
            CREATE OR REPLACE VIEW core_v1.employee AS
            SELECT id, tenant_id, company_id, public_id,
                   first_name, last_name,
                   (first_name || ' ' || last_name) AS display_name,
                   status
            FROM core.employee
            WHERE deleted = false;
            """);

        migrationBuilder.Sql("""
            CREATE OR REPLACE VIEW core_v1.company AS
            SELECT id, tenant_id, public_id, name, active
            FROM core.company;
            """);

        // Granted in the same script as the object it grants. A published view nobody can
        // select from is a broken deploy that every unit test passes.
        migrationBuilder.Sql("GRANT USAGE ON SCHEMA core_v1 TO ap_core_rt;");
        migrationBuilder.Sql("GRANT SELECT ON core_v1.employee, core_v1.company TO ap_core_rt;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS core_v1.company;");
        migrationBuilder.Sql("DROP VIEW IF EXISTS core_v1.employee;");
    }
}
}
