using AppPlatform.Auth;
using AppPlatform.Encryption;
using AppPlatform.Platform.Data;

namespace AppPlatform.Platform.Auth;

/// <summary>
/// The one place an operator's authenticator secret is written or read. The secret is encrypted
/// with the platform's key ring and bound to its operator, so it can be used for nobody else;
/// feature code never handles the ciphertext.
///
/// Enrolment happens only on the machine, through the create-platform-user and
/// reset-platform-user-mfa commands, never over the API. An operator reaches every tenant, so the
/// way to give one a second factor is the same as the way to create one: be on the machine.
/// </summary>
public static class OperatorMfa
{
    /// <summary>The name an authenticator app shows beside the code.</summary>
    public const string Issuer = "App Platform";

    /// <summary>
    /// Gives the operator a new secret and returns it in plaintext, to be shown once. Replaces any
    /// earlier secret, so a lost phone is recovered by enrolling again. Resets the replay guard,
    /// because the new secret's codes have never been used.
    /// </summary>
    public static string Enroll(PlatformUser user, KeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(keyRing);

        var secret = Totp.GenerateSecret();

        user.TotpSecretEncrypted = keyRing.Encrypt(secret, BoundTo(user));
        user.TotpSecretVersion++;
        user.TotpLastUsedStep = null;

        return secret;
    }

    public static string ReadSecret(PlatformUser user, KeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(keyRing);

        if (user.TotpSecretEncrypted is not { } encrypted)
            throw new InvalidOperationException($"Operator {user.PublicId} has no authenticator enrolled.");

        return keyRing.Decrypt(encrypted, BoundTo(user));
    }

    /// <summary>What the ciphertext is bound to: this column, of this operator's row.</summary>
    private static string BoundTo(PlatformUser user) => $"platform_user.totp_secret:{user.Id:N}";
}
