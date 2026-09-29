using AppPlatform.Auth;
using AppPlatform.Encryption;
using AppPlatform.Platform.Auth;
using AppPlatform.Ids;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform;

/// <summary>
/// Creates an operator account: <c>dotnet run -- create-platform-user &lt;email&gt;</c>, password on stdin.
///
/// There is no self-service registration and no API for this, deliberately — an operator can reach
/// every tenant, so the only way to make one is to be on the machine.
///
/// The password comes from stdin rather than an argument so it stays out of the process list. It
/// does NOT protect against shell history: `echo 'pw' | dotnet run …` is recorded like any other
/// command, and interactive entry is echoed. Intended use is a password manager piping in.
/// </summary>
public static class CreatePlatformUser
{
    /// <summary>
    /// Matches the tenant-user rule in `AcceptInviteFeature`. Duplicated rather than shared
    /// because platform.api must not reference core.api — the boundary is enforced by
    /// `BoundaryTests`. If one changes, change both deliberately.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>Wires the database and reads stdin; the decisions live in <see cref="CreateAsync"/>.</summary>
    public static async Task<int> RunAsync(string connectionString, KeyEncryptionKey kek, string[] args)
    {
        var email = args.SkipWhile(a => a != "create-platform-user").Skip(1).FirstOrDefault();
        var password = await Console.In.ReadLineAsync();

        await using var db = PlatformDbContext.ForConnection(connectionString);
        var keyRing = await KeyRing.LoadOrCreateAsync(db, kek, TimeProvider.System);

        var (exitCode, message) = await CreateAsync(db, keyRing, email, password, TimeProvider.System);

        (exitCode == 0 ? Console.Out : Console.Error).WriteLine(message);
        return exitCode;
    }

    /// <summary>
    /// Separated from stdin and connection wiring so the rules can be tested, including the one
    /// that matters most: a re-run must NOT change an existing operator's password.
    /// </summary>
    internal static async Task<(int ExitCode, string Message)> CreateAsync(
        PlatformDbContext db, KeyRing keyRing, string? emailArgument, string? password, TimeProvider clock)
    {
        var email = emailArgument?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email))
            return (1, "Usage: dotnet run -- create-platform-user <email>  (password on stdin)");

        if (password is not { Length: >= MinimumPasswordLength })
        {
            return (1,
                $"A password of at least {MinimumPasswordLength} characters is required on stdin.");
        }

        if (await db.PlatformUsers.AnyAsync(u => u.Email == email))
        {
            // Idempotent so a re-run is harmless, but it does NOT reset the password. Quietly
            // changing an existing operator's credentials from a script — one that can be re-run
            // by anyone who can run it once — is not a capability this command should have.
            return (0, $"create-platform-user: {email} already exists");
        }

        var now = clock.GetUtcNow();

        var user = new PlatformUser
        {
            PublicId = PublicId.New("op").ToString(),
            Email = email,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = now,
        };

        // Enrolled at creation, on the machine, not at first sign-in. Enrolling at first sign-in
        // would let whoever first uses the password bind THEIR authenticator; a password leaked
        // before then would hand over the second factor too.
        var secret = OperatorMfa.Enroll(user, keyRing);

        db.PlatformUsers.Add(user);

        db.AuditLogs.Add(new PlatformAuditLog
        {
            // No acting operator: this is the bootstrap case, and recording it as nobody is more
            // honest than attributing it to the account being created.
            Action = "operator.created",
            Detail = email,
            CreatedAt = now,
        });

        await db.SaveChangesAsync();

        return (0, $"create-platform-user: created {user.PublicId} ({email})\n" + SecretNotice(email, secret));
    }

    /// <summary>
    /// The secret, shown ONCE, for the operator to add to an authenticator app. It is not stored in
    /// plaintext anywhere, so it cannot be shown again; a lost one is replaced with
    /// reset-platform-user-mfa. The terminal's scrollback holds it until cleared.
    /// </summary>
    internal static string SecretNotice(string email, string secret)
        => $"{SecretLinePrefix}{secret}\n" +
           $"Authenticator link: {Totp.ProvisioningUri(OperatorMfa.Issuer, email, secret)}\n" +
           "Add it to an authenticator app now. It is not shown again.";

    /// <summary>Scripts (the e2e harness) read the secret from the line starting with this.</summary>
    internal const string SecretLinePrefix = "MFA secret (shown once): ";
}
