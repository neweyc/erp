namespace AppPlatform.Auth.Tests;

public class PasswordHasherTests
{
    // Deliberately low so the suite stays fast. Verify reads the cost from the stored hash, so
    // this also proves the format is doing its job.
    private const int TestIterations = 1_000;

    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple", TestIterations);

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void A_wrong_password_does_not_verify()
        => Assert.False(PasswordHasher.Verify("wrong", PasswordHasher.Hash("right", TestIterations)));

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // A per-password salt. Identical hashes for identical passwords would let anyone with
        // the table see which operators share one.
        var a = PasswordHasher.Hash("same", TestIterations);
        var b = PasswordHasher.Hash("same", TestIterations);

        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("same", a));
        Assert.True(PasswordHasher.Verify("same", b));
    }

    [Fact]
    public void The_cost_is_stored_with_the_hash_so_it_can_be_raised_later()
    {
        var cheap = PasswordHasher.Hash("pw", 1_000);

        // Still verifiable after the requirement rises — which is what makes raising it
        // possible at all. A bare hash with the cost baked into code cannot be upgraded,
        // so in practice it never is.
        Assert.True(PasswordHasher.Verify("pw", cheap));
        Assert.True(PasswordHasher.NeedsRehash(cheap, 600_000));
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Hash("pw", 600_000), 600_000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("v1.1000.notbase64!.abc")]
    [InlineData("v2.1000.c2FsdA==.aGFzaA==")]
    [InlineData("v1.0.c2FsdA==.aGFzaA==")]
    [InlineData("v1.1000.c2FsdA==.dG9vc2hvcnQ=")]
    public void A_malformed_or_unknown_hash_returns_false_rather_than_throwing(string? stored)
    {
        // A corrupt row must fail one sign-in, not take the endpoint down with a 500 that tells
        // an attacker which accounts have bad data.
        Assert.False(PasswordHasher.Verify("anything", stored));
    }

    [Fact]
    public void An_empty_password_is_refused_at_hashing()
        => Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(""));

    [Fact]
    public void The_default_cost_meets_the_current_floor()
    {
        // Pinned so lowering it is a deliberate edit to a test, not a quiet change to a constant.
        Assert.True(PasswordHasher.DefaultIterations >= 600_000);
    }
}
