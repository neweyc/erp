using System.Text.Json;
using AppPlatform.Audit;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Internal;
using AppPlatform.Tenancy;
using AppPlatform.Tickets.Data;
using AppPlatform.Tickets.Features.Tickets;
using AppPlatform.Tickets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Audit against real PostgreSQL, as the real runtime roles. The unit tests prove what a row
/// contains; these prove it reaches the database through the shipped grants, in the same
/// transaction as the change it records.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class AuditTrailTests(PrivilegeFixture fixture)
{
    private static readonly Guid Ada = Guid.CreateVersion7();

    private TicketsDbContext Tickets(IInterceptor? interceptor = null)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(1);

        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseNpgsql(fixture.ConnectionStringAs("ap_tickets_rt"))
            .UseSnakeCaseNamingConvention();
        if (interceptor is not null) options.AddInterceptors(interceptor);

        return new TicketsDbContext(
            options.Options,
            // In the running service this comes from CallerContext; here, the same principal.
            tenant, new AmbientAuditActor(AuditActor.User(Ada)), TimeProvider.System);
    }

    /// <summary>
    /// Fires between the audit read and the write — EF raises SavingChanges after audit has staged
    /// and before any UPDATE is sent — and tries to change the same row from another connection.
    /// </summary>
    private sealed class Interloper(string connectionString, string ticketId) : SaveChangesInterceptor
    {
        public string? SqlState { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "SET lock_timeout = '500ms'; UPDATE tickets.ticket SET title = 'Interloper' WHERE public_id = @id",
                connection);
            command.Parameters.AddWithValue("id", ticketId);

            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex)
            {
                SqlState = ex.SqlState;
            }

            return result;
        }
    }

    private static Auth.Caller Caller() => new()
    {
        PrincipalId = Ada,
        Kind = Auth.PrincipalKind.User,
        UserId = Ada,
        TenantId = 1,
        CompanyId = 1,
        Role = "member",
        LicensedApps = ["tickets"],
    };

    private static CreateTicketFeature.CreateTicketCommandHandler Create(TicketsDbContext db)
        => new(new EFTicketService(db), new EFEmployeeLookup(db),
            new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System);

    /// <summary>Read as the superuser, so the assertion does not depend on the grants under test.</summary>
    private async Task<List<(string Action, string ActorKind, Guid? ActorId, string? ActorName, JsonElement Changes)>>
        RowsFor(string schema, string entityId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT action, actor_kind, actor_id, actor_name, changes::text FROM {schema}.audit_log " +
            "WHERE entity_id = @id ORDER BY id", connection);
        command.Parameters.AddWithValue("id", entityId);

        var rows = new List<(string, string, Guid?, string?, JsonElement)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                JsonDocument.Parse(reader.GetString(4)).RootElement));
        }

        return rows;
    }

    [Fact]
    public async Task A_ticket_create_and_close_are_recorded_against_the_caller()
    {
        string ticketId;
        await using (var db = Tickets())
        {
            var created = await Create(db).Handle(Caller(), new("Audited", null, null));
            Assert.True(created.Succeeded, created.Message);
            ticketId = (await db.Tickets.SingleAsync(t => t.Title == "Audited")).PublicId;
        }

        await using (var db = Tickets())
        {
            var closed = await new CloseTicketFeature.CloseTicketCommandHandler(
                new EFTicketService(db), new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
                .Handle(Caller(), ticketId);
            Assert.True(closed.Succeeded, closed.Message);
        }

        var rows = await RowsFor("tickets", ticketId);

        Assert.Equal(["Created", "Updated"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(("User", (Guid?)Ada), (r.ActorKind, r.ActorId)));

        var status = rows[1].Changes.GetProperty("status");
        Assert.Equal("Open", status.GetProperty("old").GetString());
        Assert.Equal("Closed", status.GetProperty("new").GetString());
    }

    [Fact]
    public async Task A_change_whose_audit_row_cannot_be_written_is_not_saved_either()
    {
        // The change and its row share one transaction. Refusing the audit INSERT must take the
        // ticket with it — a ticket that exists with no record of who created it is the failure
        // this whole mechanism exists to prevent.
        await fixture.ExecuteAsync("REVOKE INSERT ON tickets.audit_log FROM ap_tickets_rt");

        try
        {
            await using var db = Tickets();
            await Assert.ThrowsAsync<DbUpdateException>(
                () => Create(db).Handle(Caller(), new("Unrecordable", null, null)));
        }
        finally
        {
            await fixture.ExecuteAsync("GRANT INSERT ON tickets.audit_log TO ap_tickets_rt");
        }

        await using var check = Tickets();
        Assert.False(await check.Tickets.AnyAsync(t => t.Title == "Unrecordable"));
    }

    [Fact]
    public async Task Provisioning_is_attributed_to_the_provisioning_process_not_to_nobody()
    {
        var tenant = new AmbientTenantProvider();
        var actor = new AmbientAuditActor();
        await using var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_core_rt"))
                .UseSnakeCaseNamingConvention().Options,
            tenant, actor, TimeProvider.System);

        var result = await new ProvisionTenantFeature.ProvisionTenantCommandHandler(
            db, tenant, actor, TimeProvider.System)
            .Handle(new("Audited Ltd", "admin@audited.test", Guid.NewGuid().ToString()));
        Assert.True(result.Succeeded, result.Message);

        var admin = await db.Users.SingleAsync(u => u.Email == "admin@audited.test");
        var company = await db.Companies.SingleAsync(c => c.Name == "Audited Ltd");

        var adminRow = Assert.Single(await RowsFor("core", admin.PublicId));
        var companyRow = Assert.Single(await RowsFor("core", company.PublicId));

        foreach (var row in new[] { adminRow, companyRow })
        {
            Assert.Equal("Created", row.Action);
            Assert.Equal("System", row.ActorKind);
            Assert.Null(row.ActorId);
            Assert.Equal("provisioning", row.ActorName);
        }

        Assert.Equal("admin", adminRow.Changes.GetProperty("role").GetProperty("new").GetString());
    }

    [Fact]
    public async Task The_row_is_locked_from_the_audit_read_until_the_write_commits()
    {
        // Regression (Codex re-review): the "before" was read outside any transaction, so another
        // writer could change the row between that read and the write, and history recorded a
        // "before" that was not what got overwritten.
        string ticketId;
        await using (var db = Tickets())
        {
            Assert.True((await Create(db).Handle(Caller(), new("Locked title", null, null))).Succeeded);
            ticketId = (await db.Tickets.SingleAsync(t => t.Title == "Locked title")).PublicId;
        }

        var interloper = new Interloper(fixture.ConnectionString, ticketId);
        await using (var db = Tickets(interloper))
        {
            var ticket = await db.Tickets.SingleAsync(t => t.PublicId == ticketId);
            ticket.Title = "Renamed under lock";
            await db.SaveChangesAsync();
        }

        // 55P03 lock_not_available: the concurrent UPDATE could not get the row while the audited
        // save held it — so the recorded "before" is exactly what was overwritten.
        Assert.Equal("55P03", interloper.SqlState);

        var title = (await RowsFor("tickets", ticketId))[1].Changes.GetProperty("title");
        Assert.Equal("Locked title", title.GetProperty("old").GetString());
        Assert.Equal("Renamed under lock", title.GetProperty("new").GetString());
    }

    /// <summary>Fails the first commit it sees, as a dropped connection or a timeout would.</summary>
    private sealed class FailFirstCommit : DbTransactionInterceptor
    {
        private bool _failed;

        private void FailOnce()
        {
            if (_failed) return;
            _failed = true;
            throw new InvalidOperationException("injected commit failure");
        }

        public override InterceptionResult TransactionCommitting(
            System.Data.Common.DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
        {
            FailOnce();
            return result;
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            FailOnce();
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_commit_leaves_the_change_pending_so_a_retry_writes_it(bool sync)
    {
        // Regression (Codex, third round): EF accepted the changes before the audit's own
        // transaction committed, so after a failed commit the tracker believed the work was saved
        // and a retry on the same context wrote nothing at all.
        var title = $"Commit test {sync}";
        string ticketId;
        await using (var db = Tickets())
        {
            Assert.True((await Create(db).Handle(Caller(), new(title, null, null))).Succeeded);
            ticketId = (await db.Tickets.SingleAsync(t => t.Title == title)).PublicId;
        }

        await using (var db = Tickets(new FailFirstCommit()))
        {
            var ticket = await db.Tickets.SingleAsync(t => t.PublicId == ticketId);
            ticket.Title = $"{title} renamed";

            if (sync) Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            else await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

            // The same context, retried: the change must still be pending, and must now be written.
            if (sync) db.SaveChanges();
            else await db.SaveChangesAsync();
        }

        await using var check = Tickets();
        Assert.Equal($"{title} renamed", (await check.Tickets.SingleAsync(t => t.PublicId == ticketId)).Title);

        // One row for the rename — not zero (lost), and not two (the failed attempt's as well).
        Assert.Equal(["Created", "Updated"], (await RowsFor("tickets", ticketId)).Select(r => r.Action));
    }

    /// <summary>Counts row-lock statements, to prove none was sent.</summary>
    private sealed class LockCounter : DbCommandInterceptor
    {
        public int Locks { get; private set; }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) Locks++;
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Another_tenants_row_is_refused_before_it_is_locked()
    {
        // Regression (Codex, third round): the lock's tenant came from the entity, so a context in
        // tenant 2 attaching a row with its genuine tenant-1 value locked and read that row before
        // the tenant guard refused the save. The guard now runs first.
        string ticketId;
        await using (var owner = Tickets())
        {
            Assert.True((await Create(owner).Handle(Caller(), new("Not yours to lock", null, null))).Succeeded);
            ticketId = (await owner.Tickets.SingleAsync(t => t.Title == "Not yours to lock")).PublicId;
        }

        await using var read = Tickets();
        var original = await read.Tickets.AsNoTracking().SingleAsync(t => t.PublicId == ticketId);

        var counter = new LockCounter();
        var tenant2 = new AmbientTenantProvider();
        tenant2.UseTenant(2);
        await using var attacker = new TicketsDbContext(
            new DbContextOptionsBuilder<TicketsDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_tickets_rt"))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(counter).Options,
            tenant2, new AmbientAuditActor(AuditActor.User(Guid.CreateVersion7())), TimeProvider.System);

        attacker.Update(new Tickets.Data.Ticket
        {
            Id = original.Id, TenantId = 1, PublicId = original.PublicId, Title = "Stolen",
            Status = original.Status, Version = original.Version, CreatedAt = original.CreatedAt,
        });

        await Assert.ThrowsAsync<TenantScopeViolationException>(() => attacker.SaveChangesAsync());
        Assert.Equal(0, counter.Locks);
    }

    [Fact]
    public async Task Re_running_the_grants_removes_a_column_level_update_on_an_audit_log()
    {
        // Regression (Codex review): a table-level REVOKE leaves a column grant in place, so the
        // repair script used to report success while the history stayed rewritable.
        await fixture.ExecuteAsync("GRANT UPDATE (changes) ON core.audit_log TO ap_core_rt");
        Assert.Null(await fixture.TryAsAsync("ap_core_rt", "UPDATE core.audit_log SET changes = '{}' WHERE false"));

        await fixture.ReapplyGrantsAsync();

        Assert.Equal("42501", await fixture.TryAsAsync("ap_core_rt", "UPDATE core.audit_log SET changes = '{}' WHERE false"));
        Assert.Equal(0, await fixture.VerificationFindingsAsync());
    }

    [Fact]
    public async Task A_forged_detached_write_to_another_tenants_ticket_is_refused_and_leaves_no_row()
    {
        string ticketId;
        await using (var owner = Tickets())
        {
            var created = await Create(owner).Handle(Caller(), new("Owned by tenant 1", null, null));
            Assert.True(created.Succeeded, created.Message);
            ticketId = (await owner.Tickets.SingleAsync(t => t.Title == "Owned by tenant 1")).PublicId;
        }

        var original = await Tickets().Tickets.AsNoTracking().SingleAsync(t => t.PublicId == ticketId);

        // Tenant 2, holding tenant 1's row id, relabels it with its own tenant and attaches it.
        var tenant2 = new AmbientTenantProvider();
        tenant2.UseTenant(2);
        await using (var attacker = new TicketsDbContext(
            new DbContextOptionsBuilder<TicketsDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_tickets_rt"))
                .UseSnakeCaseNamingConvention().Options,
            tenant2, new AmbientAuditActor(AuditActor.User(Guid.CreateVersion7())), TimeProvider.System))
        {
            var forged = new Tickets.Data.Ticket
            {
                Id = original.Id, TenantId = 2, PublicId = original.PublicId, Title = "Stolen",
                Status = original.Status, Version = original.Version, CreatedAt = original.CreatedAt,
            };
            attacker.Update(forged);

            // Refused by audit before the UPDATE is sent: the tenant-scoped lock finds no row for
            // tenant 2, so nothing of tenant 1's is read, locked, or copied into a staged row.
            // Raised as a concurrency conflict — the same answer the forged UPDATE itself would
            // get from its tenant predicate, as TenantWriteIsolationTests documents.
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => attacker.SaveChangesAsync());
        }

        var after = await Tickets().Tickets.AsNoTracking().SingleAsync(t => t.PublicId == ticketId);
        Assert.Equal("Owned by tenant 1", after.Title);
        Assert.Equal(["Created"], (await RowsFor("tickets", ticketId)).Select(r => r.Action));
    }
}
