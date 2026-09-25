using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.Api;

/// <summary>
/// Recognises a losing write in a race that a preflight read cannot prevent.
///
/// One place, used by every call site that needs it: provisioning idempotency, entitlement grants,
/// and ticket assignment and closure. The pattern is always the same — read, decide, write — and
/// the read cannot stop two callers both deciding to insert. The unique index is the guard, and a
/// 23505 is its expected outcome rather than an error.
/// </summary>
public static class DatabaseConflict
{
    private const string UniqueViolation = "23505";

    public static bool IsUniqueViolation(Exception ex) => ex switch
    {
        DbUpdateException { InnerException: PostgresException { SqlState: UniqueViolation } } => true,
        PostgresException { SqlState: UniqueViolation } => true,
        _ => false,
    };

    /// <summary>
    /// Also true for an optimistic-concurrency failure, where EF's version predicate matched no
    /// row. Both mean "someone else got there first".
    /// </summary>
    public static bool IsLostRace(Exception ex)
        => ex is DbUpdateConcurrencyException || IsUniqueViolation(ex);
}
