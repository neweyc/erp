using AppPlatform.Auth;
using AppPlatform.Ids;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform;

/// <summary>
/// Creates an operator account: <c>dotnet run -- create-platform-user &lt;email&gt;</c>, password on stdin.
///
/// There is no self-service registration and no API for this, deliberately — an operator can reach
/// every tenant, so the only way to make one is to be on the machine. The password comes from
/// stdin rather than an argument so it does not land in shell history or a process list.
/// </summary>
public static class CreatePlatformUser
{
    /// <summary>Matches the tenant-user rule. Length is the requirement that reliably helps.</summary>
    public const int MinimumPasswordLength = 12;

    public static async Task<int> RunAsync(string connectionString, string[] args)
    {
        var email = args.SkipWhile(a => a != "create-platform-user").Skip(1).FirstOrDefault()
            ?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Error.WriteLine("Usage: dotnet run -- create-platform-user <email>  (password on stdin)");
            return 1;
        }

        var password = await Console.In.ReadLineAsync();

        if (password is not { Length: >= MinimumPasswordLength })
        {
            Console.Error.WriteLine(
                $"A password of at least {MinimumPasswordLength} characters is required on stdin.");
            return 1;
        }

        await using var db = new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options);

        if (await db.PlatformUsers.AnyAsync(u => u.Email == email))
        {
            // Idempotent so a re-run is harmless, but it does NOT reset the password: quietly
            // changing an existing operator's credentials from a script is not something this
            // command should be able to do.
            Console.WriteLine($"create-platform-user: {email} already exists");
            return 0;
        }

        var user = new PlatformUser
        {
            PublicId = PublicId.New("op").ToString(),
            Email = email,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.PlatformUsers.Add(user);

        db.AuditLogs.Add(new PlatformAuditLog
        {
            // No acting operator: this is the bootstrap case, and recording it as nobody is more
            // honest than attributing it to the account being created.
            Action = "operator.created",
            Detail = email,
            CreatedAt = user.CreatedAt,
        });

        await db.SaveChangesAsync();

        Console.WriteLine($"create-platform-user: created {user.PublicId} ({email})");
        return 0;
    }
}
