using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AppPlatform.Tenancy;

/// <summary>
/// A row that is written once and never changed or removed — a posted journal, an audit entry.
/// A mistake in one is corrected by writing ANOTHER row (a reversal), never by editing history.
///
/// Enforced twice. <see cref="AppendOnlyGuard"/> refuses the save in code, so the mistake shows up
/// in a unit test; the table is also listed in database/privileges/02-grants.sql, which removes
/// UPDATE and DELETE from the runtime role, so it holds against code that is not in this repo.
/// BoundaryTests fails if an IAppendOnly table is missing from that list.
/// </summary>
public interface IAppendOnly;

public sealed class AppendOnlyViolationException(string message) : InvalidOperationException(message);

public static class AppendOnlyGuard
{
    /// <summary>Refuses a save that would modify or delete an <see cref="IAppendOnly"/> row.</summary>
    public static void Enforce(ChangeTracker changeTracker)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);

        var violation = changeTracker.Entries()
            .FirstOrDefault(e => e.Entity is IAppendOnly && e.State is EntityState.Modified or EntityState.Deleted);

        if (violation is not null)
        {
            throw new AppendOnlyViolationException(
                $"{violation.Metadata.ClrType.Name} is append-only: it cannot be " +
                $"{(violation.State == EntityState.Deleted ? "deleted" : "modified")}. " +
                "Correct it by writing a new row, such as a reversal.");
        }
    }
}
