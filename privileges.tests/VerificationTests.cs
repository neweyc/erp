namespace AppPlatform.PrivilegeTests;

/// <summary>
/// 99-verify.sql reports only violations, so a clean database yields no rows. Which means
/// a broken verifier is indistinguishable from a correct database — every test here
/// exists to tell those two apart.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class VerificationTests(PrivilegeFixture fixture)
{
    [Fact]
    public async Task A_correctly_bootstrapped_database_reports_nothing()
        => Assert.Equal(0, await fixture.VerificationFindingsAsync());

    [Theory]
    // Each pair breaks one rule, then puts it back. The restore is asserted too: a
    // break that silently failed to apply would otherwise look like a passing check.
    [InlineData("GRANT USAGE ON SCHEMA core TO ap_platform_rt",
                "REVOKE USAGE ON SCHEMA core FROM ap_platform_rt")]
    [InlineData("GRANT UPDATE ON platform.tenant TO ap_core_rt",
                "REVOKE UPDATE ON platform.tenant FROM ap_core_rt")]
    [InlineData("GRANT CREATE ON SCHEMA tickets TO ap_tickets_rt",
                "REVOKE CREATE ON SCHEMA tickets FROM ap_tickets_rt")]
    [InlineData("CREATE TABLE public.stray (id int)", "DROP TABLE public.stray")]
    [InlineData("CREATE TABLE core_v1.stray (id int)", "DROP TABLE core_v1.stray")]
    [InlineData("CREATE VIEW core_v1.bad WITH (security_invoker=true) AS SELECT 1 AS x",
                "DROP VIEW core_v1.bad")]
    [InlineData("CREATE FUNCTION core_v1.bad_fn() RETURNS int LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
                "DROP FUNCTION core_v1.bad_fn()")]
    [InlineData("CREATE VIEW core_v1.misowned AS SELECT 1 AS x", "DROP VIEW core_v1.misowned")]
    [InlineData("GRANT EXECUTE ON FUNCTION identity_v1.session_context(uuid) TO ap_platform_rt",
                "REVOKE EXECUTE ON FUNCTION identity_v1.session_context(uuid) FROM ap_platform_rt")]
    [InlineData("ALTER ROLE ap_tickets_rt SUPERUSER", "ALTER ROLE ap_tickets_rt NOSUPERUSER")]
    [InlineData("GRANT UPDATE ON core.audit_log TO ap_core_rt",
                "REVOKE UPDATE ON core.audit_log FROM ap_core_rt")]
    [InlineData("GRANT DELETE ON tickets.audit_log TO ap_tickets_rt",
                "REVOKE DELETE ON tickets.audit_log FROM ap_tickets_rt")]
    [InlineData("GRANT TRUNCATE ON platform.audit_log TO ap_platform_rt",
                "REVOKE TRUNCATE ON platform.audit_log FROM ap_platform_rt")]
    // A COLUMN grant: invisible to has_table_privilege, and enough to rewrite history.
    [InlineData("GRANT UPDATE (changes) ON core.audit_log TO ap_core_rt",
                "REVOKE UPDATE (changes) ON core.audit_log FROM ap_core_rt")]
    public async Task Breaking_a_rule_is_reported_and_repairing_it_clears(string breakSql, string repairSql)
    {
        await fixture.ExecuteAsync(breakSql);
        var whileBroken = await fixture.VerificationFindingsAsync();

        await fixture.ExecuteAsync(repairSql);
        var afterRepair = await fixture.VerificationFindingsAsync();

        Assert.True(whileBroken > 0, $"'{breakSql}' was not reported by 99-verify.sql");
        Assert.Equal(0, afterRepair);
    }
}
