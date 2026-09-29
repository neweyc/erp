namespace AppPlatform.Auth.Tests;

public class TotpTests
{
    /// <summary>The RFC 6238 appendix B SHA-1 secret: the ASCII bytes of "12345678901234567890".</summary>
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static DateTimeOffset At(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

    [Theory]
    // RFC 6238 appendix B publishes 8-digit codes; a 6-digit code is the same value's last six.
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    [InlineData(20000000000, "353130")]
    public void Codes_match_the_RFC_6238_test_vectors(long unixSeconds, string expected)
        => Assert.Equal(expected, Totp.CodeAt(RfcSecret, Totp.StepAt(At(unixSeconds))));

    [Fact]
    public void The_current_code_is_accepted_and_its_step_returned()
    {
        var now = At(1234567890);

        Assert.Equal(Totp.StepAt(now), Totp.Verify(RfcSecret, "005924", now, lastUsedStep: null));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void A_code_one_step_either_side_is_accepted_for_clock_drift(int offset)
    {
        var now = At(1234567890);
        var code = Totp.CodeAt(RfcSecret, Totp.StepAt(now) + offset);

        Assert.Equal(Totp.StepAt(now) + offset, Totp.Verify(RfcSecret, code, now, null));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void A_code_two_steps_away_is_refused(int offset)
    {
        var now = At(1234567890);
        var code = Totp.CodeAt(RfcSecret, Totp.StepAt(now) + offset);

        Assert.Null(Totp.Verify(RfcSecret, code, now, null));
    }

    [Fact]
    public void A_code_is_accepted_once()
    {
        // RFC 6238 §5.2: a code seen over a shoulder must not work a second time while it is valid.
        var now = At(1234567890);
        var used = Totp.Verify(RfcSecret, "005924", now, null);

        Assert.Null(Totp.Verify(RfcSecret, "005924", now, lastUsedStep: used));
    }

    [Fact]
    public void An_earlier_step_cannot_be_used_after_a_later_one()
    {
        var now = At(1234567890);
        var previous = Totp.CodeAt(RfcSecret, Totp.StepAt(now) - 1);

        Assert.Null(Totp.Verify(RfcSecret, previous, now, lastUsedStep: Totp.StepAt(now)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("000000")]
    public void Anything_but_the_right_six_digits_is_refused(string? code)
        => Assert.Null(Totp.Verify(RfcSecret, code, At(1234567890), null));

    [Fact]
    public void Spaces_typed_in_the_middle_of_a_code_are_ignored()
        => Assert.NotNull(Totp.Verify(RfcSecret, "005 924", At(1234567890), null));

    [Fact]
    public void A_generated_secret_is_160_bits_of_base32_and_round_trips()
    {
        var secret = Totp.GenerateSecret();

        Assert.Equal(32, secret.Length);
        Assert.Equal(20, Totp.Base32.Decode(secret).Length);
        Assert.Equal(secret, Totp.Base32.Encode(Totp.Base32.Decode(secret)));
        Assert.NotEqual(secret, Totp.GenerateSecret());
    }

    [Fact]
    public void The_provisioning_uri_is_what_authenticator_apps_scan()
        => Assert.Equal(
            "otpauth://totp/App%20Platform:op%40example.com?secret=ABC&issuer=App%20Platform&digits=6&period=30",
            Totp.ProvisioningUri("App Platform", "op@example.com", "ABC"));
}
