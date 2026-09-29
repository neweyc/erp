using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Platform.Migrations
{
    /// <summary>
    /// APPLY AS ap_owner, not as ap_platform_migrate: this migration creates a published object
    /// (database/README.md). A SECURITY DEFINER function executes with its owner's rights, so it must
    /// be owned by ap_owner, and only ap_owner can create the platform_v1 schema. Kept apart from
    /// AddErrorFeed, the table, which is an ordinary migration for the platform's migration role.
    /// 99-verify.sql reports the function if it ends up owned by anyone else.
    /// </summary>
    public partial class AddErrorFeedPublishedFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The published way INTO the feed, for every service. A FUNCTION, not a table grant: it
            // checks each value's shape and stores nothing else, and a role holding only EXECUTE on it
            // can add to the feed without being able to read it. The same pattern as core's published
            // session lookup, in the other direction.
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS platform_v1;");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION platform_v1.record_error(
                  p_reference text, p_fingerprint text, p_app text, p_tenant_id int, p_count int,
                  p_occurred_at timestamptz)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $fn$
                BEGIN
                  -- Every column allowlisted by shape. Anything else is refused rather than stored:
                  -- this table is metadata only, and free text is where customer data would get in.
                  IF p_reference IS NULL OR p_reference !~ '^err_[0-9a-z]{25}$'
                     OR p_fingerprint IS NULL OR p_fingerprint !~ '^[0-9a-f]{16}$'
                     OR p_app IS NULL OR p_app !~ '^[a-z][a-z0-9_]{1,19}$'
                     OR p_count IS NULL OR p_count NOT BETWEEN 1 AND 100000
                     OR p_occurred_at IS NULL
                     OR p_occurred_at NOT BETWEEN now() - interval '1 day' AND now() + interval '5 minutes'
                  THEN
                    RAISE EXCEPTION 'platform_v1.record_error refused a malformed occurrence'
                      USING ERRCODE = 'invalid_parameter_value';
                  END IF;

                  INSERT INTO platform.error_occurrence (reference, fingerprint, app, tenant_id, count, occurred_at)
                  VALUES (p_reference, p_fingerprint, p_app, p_tenant_id, p_count, p_occurred_at);
                END
                $fn$;
                """);

            // Granted in the same script as the object, after revoking PUBLIC's default EXECUTE.
            migrationBuilder.Sql("REVOKE EXECUTE ON FUNCTION platform_v1.record_error(text, text, text, int, int, timestamptz) FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA platform_v1 TO ap_platform_rt, ap_core_rt, ap_tickets_rt, ap_ledger_rt;");
            migrationBuilder.Sql("GRANT EXECUTE ON FUNCTION platform_v1.record_error(text, text, text, int, int, timestamptz) TO ap_platform_rt, ap_core_rt, ap_tickets_rt, ap_ledger_rt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform_v1.record_error(text, text, text, int, int, timestamptz);");
        }
    }
}
