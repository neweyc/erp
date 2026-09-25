namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The boundary as behaviour rather than as a grant listing. Every case states the role,
/// the statement, and whether it must succeed — because a matrix of only the permitted
/// cases passes cleanly against a database where every role can do everything.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class AccessMatrixTests(PrivilegeFixture fixture)
{
    private const string InsufficientPrivilege = "42501";

    public static TheoryData<string, string, string, bool> Cases => new()
    {
        // The published-view mechanism: readable through the contract, not around it.
        { "an app reads the published employee view", "ap_tickets_rt",
          "SELECT display_name FROM core_v1.employee", true },
        { "an app cannot read core's own table", "ap_tickets_rt",
          "SELECT * FROM core.employee", false },
        { "an app cannot reach identity", "ap_tickets_rt",
          "SELECT * FROM identity.session", false },
        { "an app cannot reach the platform", "ap_tickets_rt",
          "SELECT * FROM platform.tenant", false },

        // The provisioning exception, split by operation: core can create a tenant,
        // only the operator can change what it is permitted to do.
        { "core creates a tenant", "ap_core_rt",
          // created_at is NOT NULL with no default in the shipped schema, so a real INSERT must
          // supply it. The old hand-written fixture omitted the column entirely.
          "INSERT INTO platform.tenant (public_id, name, status, created_at) " +
          "VALUES ('ten_z','Z','Active', now())", true },
        { "core cannot change a tenant's status", "ap_core_rt",
          "UPDATE platform.tenant SET status = 'suspended'", false },
        { "core cannot delete a tenant", "ap_core_rt",
          "DELETE FROM platform.tenant", false },
        { "core cannot read entitlements", "ap_core_rt",
          "SELECT * FROM platform.tenant_app", false },
        { "core cannot read the operator audit log", "ap_core_rt",
          "SELECT * FROM platform.audit_log", false },

        // THE platform boundary. Encrypted columns are not what protects these.
        { "the platform cannot read employees", "ap_platform_rt",
          "SELECT * FROM core.employee", false },
        { "the platform cannot read the published view either", "ap_platform_rt",
          "SELECT * FROM core_v1.employee", false },
        { "the platform cannot read tickets", "ap_platform_rt",
          "SELECT * FROM tickets.ticket", false },
        { "the platform reads its own tables", "ap_platform_rt",
          "SELECT name FROM platform.tenant", true },

        // Session resolution is a keyed function, granted to customer roles only.
        { "core resolves a session", "ap_core_rt",
          "SELECT * FROM identity_v1.session_context('33333333-3333-3333-3333-333333333333')", true },
        { "an app resolves a session", "ap_tickets_rt",
          "SELECT * FROM identity_v1.session_context('33333333-3333-3333-3333-333333333333')", true },
        { "the platform cannot resolve a tenant session", "ap_platform_rt",
          "SELECT * FROM identity_v1.session_context('33333333-3333-3333-3333-333333333333')", false },
        { "core touches a session", "ap_core_rt",
          "SELECT identity_v1.touch_session('33333333-3333-3333-3333-333333333333')", true },
        { "an app touches a session", "ap_tickets_rt",
          "SELECT identity_v1.touch_session('33333333-3333-3333-3333-333333333333')", true },
        { "the platform cannot touch a tenant session", "ap_platform_rt",
          "SELECT identity_v1.touch_session('33333333-3333-3333-3333-333333333333')", false },
        { "an app cannot update the session table directly", "ap_tickets_rt",
          "UPDATE identity.session SET last_seen_at = now()", false },

        // Audit logs are append-only: each service writes and reads its own, and none can
        // rewrite one — not even the service whose history it is.
        { "core reads its audit log", "ap_core_rt", "SELECT * FROM core.audit_log", true },
        { "core appends to its audit log", "ap_core_rt",
          "INSERT INTO core.audit_log (tenant_id, occurred_at, actor_kind, action, entity_type, entity_id, changes) " +
          "VALUES (1, now(), 'System', 'Created', 'probe', 'probe', '{}')", true },
        { "core cannot rewrite its audit log", "ap_core_rt", "UPDATE core.audit_log SET changes = '{}'", false },
        { "core cannot delete from its audit log", "ap_core_rt", "DELETE FROM core.audit_log", false },
        { "core cannot truncate its audit log", "ap_core_rt", "TRUNCATE core.audit_log", false },
        { "an app cannot rewrite its audit log", "ap_tickets_rt", "UPDATE tickets.audit_log SET changes = '{}'", false },
        { "an app cannot delete from its audit log", "ap_tickets_rt", "DELETE FROM tickets.audit_log", false },
        { "the platform cannot rewrite the operator audit log", "ap_platform_rt",
          "UPDATE platform.audit_log SET detail = detail", false },
        { "the platform cannot delete from the operator audit log", "ap_platform_rt",
          "DELETE FROM platform.audit_log", false },

        // No runtime role has DDL. A bug cannot drop a table.
        { "an app cannot create a table", "ap_tickets_rt", "CREATE TABLE tickets.nope (id int)", false },
        { "an app cannot drop its own table", "ap_tickets_rt", "DROP TABLE tickets.ticket", false },
        { "core cannot create a table", "ap_core_rt", "CREATE TABLE core.nope (id int)", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Access_matches_the_documented_boundary(
        string description, string role, string sql, bool shouldSucceed)
    {
        var error = await fixture.TryAsAsync(role, sql);

        if (shouldSucceed)
        {
            Assert.True(error is null, $"{description}: expected success, got SQLSTATE {error}");
        }
        else
        {
            Assert.True(error == InsufficientPrivilege,
                $"{description}: expected insufficient privilege, got {error ?? "success"}");
        }
    }
}
