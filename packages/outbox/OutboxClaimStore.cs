using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Outbox;

public interface IOutboxClaimStore
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due messages, taking a lease on each so no
    /// other worker picks them up.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>
/// The claim, in PostgreSQL, because this is one of the few places where the database is doing
/// something the application cannot.
///
/// **What makes it safe is that it is ONE statement.** A single
/// <c>UPDATE ... WHERE id IN (SELECT ... FOR UPDATE)</c> cannot have two executions lease the
/// same row: the second blocks on the row lock, then re-evaluates its predicate and finds
/// <c>locked_until</c> already set. A "select, then update" pair written in application code
/// has a race between the two statements however carefully it is guarded, and that race is
/// exactly "every message delivered twice".
///
/// **<c>SKIP LOCKED</c> is throughput, not correctness** — verified by removing it, which left
/// the concurrency test passing. Without it the second worker BLOCKS until the first commits
/// and then claims nothing, so a batch is still never double-claimed; it just serialises the
/// workers and can stall one behind a slow transaction. Worth having, and worth not
/// mis-crediting: if the safety story were "SKIP LOCKED", someone would eventually move the
/// locking into application code and keep the keyword.
/// </summary>
public sealed partial class OutboxClaimStore : IOutboxClaimStore
{
    private readonly DbContext _context;
    private readonly string _schema;

    public OutboxClaimStore(DbContext context, string schema)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        // The schema is interpolated into SQL because an identifier cannot be parameterised.
        // It comes from configuration rather than a request, but validating it here means the
        // day someone wires it to something user-supplied it fails loudly instead of becoming
        // an injection point.
        if (!SafeIdentifier().IsMatch(schema))
            throw new ArgumentException($"'{schema}' is not a valid schema identifier.", nameof(schema));

        _context = context;
        _schema = schema;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SafeIdentifier();

    /// <summary>
    /// The claim statement, exposed so a test can confirm the schema was interpolated and the
    /// SKIP LOCKED clause is present. Both are silent when wrong: an uninterpolated schema
    /// fails only at runtime, and a missing SKIP LOCKED simply makes workers block — or
    /// double-send — under load nobody reproduces locally.
    /// </summary>
    internal string ClaimSql => BuildClaimSql();

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var lockedUntil = now + OutboxBackoff.LeaseDuration;

        // Ordered by next_attempt_at so the longest-waiting message goes first; without it a
        // steady trickle of new work can starve a message that has already failed once.
        var sql = BuildClaimSql();

        var claimed = await _context.Database
            .SqlQueryRaw<Guid>(sql, lockedUntil, now, batchSize)
            .ToListAsync(cancellationToken);

        if (claimed.Count == 0) return [];

        // Loaded through the tenant-filtered context afterwards. The claim is deliberately
        // tenant-blind — a worker serves its whole schema — but everything the worker then
        // does with a message goes through the ordinary filters.
        return await _context.Set<OutboxMessage>()
            .IgnoreQueryFilters()
            .Where(m => claimed.Contains(m.Id))
            .OrderBy(m => m.NextAttemptAt)
            .ToListAsync(cancellationToken);
    }

    private string BuildClaimSql() => $$"""
        UPDATE {{_schema}}.outbox_message m
        SET locked_until = {0}
        WHERE m.id IN (
            SELECT id FROM {{_schema}}.outbox_message
            WHERE status = 'Pending'
              AND next_attempt_at <= {1}
              AND (locked_until IS NULL OR locked_until < {1})
            ORDER BY next_attempt_at
            LIMIT {2}
            FOR UPDATE SKIP LOCKED
        )
        RETURNING m.id
        """;
}
