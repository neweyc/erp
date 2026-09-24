namespace AppPlatform.PrivilegeTests;

[Collection(nameof(PrivilegeCollection))]
public class FunctionDefaultPrivilegeTests(PrivilegeFixture fixture)
{
    [Fact]
    public async Task New_function_is_private_until_explicitly_granted()
    {
        const string name = "identity_v1.default_privilege_probe";
        await fixture.ExecuteAsync($"SET ROLE ap_owner; CREATE FUNCTION {name}() RETURNS int LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS 'SELECT 1'; RESET ROLE;");
        try
        {
            Assert.Equal("42501", await fixture.TryAsAsync("ap_tickets_rt", $"SELECT {name}()"));
            Assert.Equal("42501", await fixture.TryAsAsync("ap_core_rt", $"SELECT {name}()"));
            await fixture.ExecuteAsync($"GRANT EXECUTE ON FUNCTION {name}() TO ap_tickets_rt");
            Assert.Null(await fixture.TryAsAsync("ap_tickets_rt", $"SELECT {name}()"));
            Assert.Equal("42501", await fixture.TryAsAsync("ap_core_rt", $"SELECT {name}()"));
        }
        finally
        {
            await fixture.ExecuteAsync($"DROP FUNCTION {name}()");
        }
    }
}
