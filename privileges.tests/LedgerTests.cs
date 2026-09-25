using AppPlatform.Api;
using AppPlatform.Audit;
using AppPlatform.Ledger.Data;
using AppPlatform.Ledger.Features;
using AppPlatform.Ledger.Features.Accounts;
using AppPlatform.Ledger.Features.Journal;
using AppPlatform.Ledger.Services;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The ledger's invariants where they can actually fail: real PostgreSQL, as the ledger's runtime
/// role. Each test gets a tenant and company of its own, so entry numbers and balances cannot leak
/// between tests.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class LedgerTests(PrivilegeFixture fixture)
{
    private static readonly Guid Ada = Guid.CreateVersion7();
    private static readonly DateOnly Day = new(2026, 9, 25);

    private sealed record Books(int TenantId, int CompanyId);

    /// <summary>A fresh tenant with one company, created as the superuser (core owns both rows).</summary>
    private async Task<Books> NewBooksAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var key = Guid.NewGuid().ToString("N")[..12];

        await using var command = new NpgsqlCommand($"""
            WITH t AS (
              INSERT INTO platform.tenant (public_id, name, status, created_at)
              VALUES ('ten_{key}', 'Books {key}', 'Active', now()) RETURNING id)
            INSERT INTO core.company (tenant_id, public_id, name, active)
            SELECT id, 'co_{key}', 'Books {key} Ltd', true FROM t
            RETURNING tenant_id, id
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new Books(reader.GetInt32(0), reader.GetInt32(1));
    }

    private LedgerDbContext Open(Books books)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(books.TenantId);

        return new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_ledger_rt"))
                .UseSnakeCaseNamingConvention().Options,
            tenant, new AmbientAuditActor(AuditActor.User(Ada)), TimeProvider.System);
    }

    /// <summary>
    /// The service over <paramref name="db"/>, with the same tenant the context filters by — in the
    /// running service both come from one CallerContext.
    /// </summary>
    private static EFLedgerService Service(Books books, LedgerDbContext db)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(books.TenantId);
        return new(db, tenant);
    }

    private static Auth.Caller Caller(Books books) => new()
    {
        PrincipalId = Ada, Kind = Auth.PrincipalKind.User, UserId = Ada,
        TenantId = books.TenantId, CompanyId = books.CompanyId, Role = "admin", LicensedApps = ["ledger"],
    };

    private async Task<string> AccountAsync(Books books, string code, string type, string? companyPublicId = null)
    {
        await using var db = Open(books);
        var result = await new CreateAccountFeature.CreateAccountCommandHandler(
            Service(books, db), new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
            .Handle(Caller(books), new(code, $"Account {code}", type, companyPublicId));

        Assert.True(result.Succeeded, result.Message);
        // From the response, not by code: codes are unique per company, not per tenant.
        return (string)result.Value!.GetType().GetProperty("accountId")!.GetValue(result.Value)!;
    }

    private Task<CommandResult> PostAsync(Books books, params (string Account, long Amount)[] lines)
        => PostWithKeyAsync(books, Guid.NewGuid().ToString(), lines);

    private async Task<CommandResult> PostWithKeyAsync(
        Books books, string idempotencyKey, params (string Account, long Amount)[] lines)
    {
        await using var db = Open(books);
        return await new PostJournalEntryFeature.PostJournalEntryCommandHandler(
            Service(books, db), new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
            .Handle(Caller(books), new(Day, "Test entry", "USD",
                [.. lines.Select(l => new PostJournalEntryFeature.LineCommand(l.Account, l.Amount, null))],
                idempotencyKey));
    }

    private async Task<CommandResult> ReverseAsync(Books books, string entryId)
    {
        await using var db = Open(books);
        return await new ReverseJournalEntryFeature.ReverseJournalEntryCommandHandler(
            Service(books, db), new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
            .Handle(Caller(books), entryId, new(Day, null));
    }

    private static string EntryId(CommandResult posted)
        => (string)posted.Value!.GetType().GetProperty("entryId")!.GetValue(posted.Value)!;

    private async Task<List<int>> NumbersAsync(Books books)
    {
        await using var db = Open(books);
        return await db.Entries.OrderBy(e => e.Number).Select(e => e.Number).ToListAsync();
    }

    [Fact]
    public async Task Posted_entries_produce_a_trial_balance_whose_columns_agree()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");
        var rent = await AccountAsync(books, "6000", "expense");

        Assert.True((await PostAsync(books, (cash, 50_000), (sales, -50_000))).Succeeded);
        Assert.True((await PostAsync(books, (rent, 12_345), (cash, -12_345))).Succeeded);

        await using var db = Open(books);
        var result = await new GetTrialBalanceFeature.GetTrialBalanceQueryHandler(Service(books, db), TimeProvider.System)
            .Handle(Caller(books), Day, null);

        var usd = Assert.Single(((GetTrialBalanceFeature.TrialBalanceModel)result.Value!).Currencies);
        Assert.Equal(50_000, usd.TotalDebitMinor);
        Assert.Equal(usd.TotalDebitMinor, usd.TotalCreditMinor);

        var byCode = usd.Accounts.ToDictionary(a => a.Code);
        Assert.Equal(37_655, byCode["1000"].DebitMinor);
        Assert.Equal(50_000, byCode["4000"].CreditMinor);
        Assert.Equal(12_345, byCode["6000"].DebitMinor);
    }

    [Fact]
    public async Task Entry_numbers_stay_gapless_when_posts_race_and_some_fail()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");

        await using (var db = Open(books))
        {
            // Resolved once, so every racer posts to the same accounts.
            var ids = await db.Accounts.ToDictionaryAsync(a => a.PublicId, a => a.Id);
            var (cashId, salesId) = (ids[cash], ids[sales]);

            // Fifteen balanced posts and five UNBALANCED ones, all at once. The unbalanced ones go
            // straight to Posting — past the handler's check — so each takes a number and is then
            // refused by the database's balance trigger at COMMIT. Their numbers must come back.
            var posts = Enumerable.Range(0, 20).Select(async i =>
            {
                await using var racer = Open(books);
                var ledger = Service(books, racer);
                var outbox = new Outbox.Outbox(racer, TimeProvider.System);
                long credit = i % 4 == 3 ? -1 : -100;

                try
                {
                    await Posting.PostAsync(ledger, outbox, TimeProvider.System, books.CompanyId, Day,
                        $"Race {i}", "USD", [new(cashId, 100, null), new(salesId, credit, null)], null, default);
                    return true;
                }
                // Raised at COMMIT, where the deferred trigger runs — so as the database error itself,
                // not wrapped by EF.
                catch (PostgresException ex) when (ex.SqlState == "23514")
                {
                    return false;
                }
            });

            var outcomes = await Task.WhenAll(posts);
            Assert.Equal(15, outcomes.Count(ok => ok));
        }

        Assert.Equal(Enumerable.Range(1, 15), await NumbersAsync(books));
    }

    [Fact]
    public async Task The_database_refuses_an_unbalanced_entry_even_when_the_handler_is_bypassed()
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();

        // Raw SQL as the ledger's own runtime role, correct in every respect but one: off by a cent.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            RawEntry(books, 1, (cash, 100), (sales, -99))));

        Assert.Equal("23514", ex.SqlState);
        Assert.Contains("unbalanced", ex.MessageText);
        Assert.Empty(await NumbersAsync(books));
    }

    [Fact]
    public async Task A_posted_entry_cannot_be_edited_by_the_code_or_the_database()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");
        Assert.True((await PostAsync(books, (cash, 100), (sales, -100))).Succeeded);

        await using (var db = Open(books))
        {
            var line = await db.Lines.FirstAsync();
            line.AmountMinor = 1;
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
        }

        Assert.Equal("42501", await fixture.TryAsAsync("ap_ledger_rt",
            $"UPDATE ledger.journal_line SET amount_minor = 1 WHERE tenant_id = {books.TenantId}"));
        Assert.Equal("42501", await fixture.TryAsAsync("ap_ledger_rt",
            $"DELETE FROM ledger.journal_entry WHERE tenant_id = {books.TenantId}"));
    }

    [Fact]
    public async Task Two_racing_reversals_of_one_entry_yield_exactly_one()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");
        var entryId = EntryId(await PostAsync(books, (cash, 100), (sales, -100)));

        var results = await Task.WhenAll(ReverseAsync(books, entryId), ReverseAsync(books, entryId));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        var loser = results.Single(r => !r.Succeeded);
        Assert.Equal(LedgerProblems.AlreadyReversed, loser.ProblemCode);

        // The original and one reversal — and the loser's number was given back, so no gap.
        Assert.Equal([1, 2], await NumbersAsync(books));
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");
        var entryId = EntryId(await PostAsync(books, (cash, 100), (sales, -100)));
        var reversalId = EntryId(await ReverseAsync(books, entryId));

        var again = await ReverseAsync(books, reversalId);

        Assert.Equal(LedgerProblems.CannotReverseAReversal, again.ProblemCode);
    }

    [Fact]
    public async Task Another_tenants_account_cannot_be_posted_to()
    {
        var a = await NewBooksAsync();
        var b = await NewBooksAsync();
        var theirs = await AccountAsync(a, "1000", "asset");
        var mine = await AccountAsync(b, "1000", "asset");

        // Through the handler: resolve-never-trust finds nothing.
        var posted = await PostAsync(b, (mine, 100), (theirs, -100));
        Assert.Equal(LedgerProblems.AccountNotFound, posted.ProblemCode);

        // Around the handler: a VALID tenant-B entry and line, except that one line names tenant A's
        // account. Only the account key can fail, and it must be the one that does.
        var theirId = await AccountIdAsync(a, theirs);
        var mineId = await AccountIdAsync(b, mine);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            RawEntry(b, number: 1, (mineId, 100), (theirId, -100), companyOfLine2: b.CompanyId)));
        Assert.Equal("23503", ex.SqlState);
        Assert.Equal("fk_journal_line_account_tenant_id_company_id_account_id", ex.ConstraintName);
    }

    private async Task<Guid> AccountIdAsync(Books books, string publicId)
    {
        await using var db = Open(books);
        return (await db.Accounts.SingleAsync(x => x.PublicId == publicId)).Id;
    }

    /// <summary>
    /// A complete entry written as raw SQL by the ledger's own role, bypassing every handler: the
    /// counter advanced, the entry, and two lines, in one transaction. Each parameter can be bent to
    /// break exactly one rule.
    /// </summary>
    private static string RawEntry(
        Books books, int number, (Guid Account, long Amount) line1, (Guid Account, long Amount) line2,
        int? companyOfLine2 = null, int? fiscalYear = null, bool advanceCounter = true, string? reverses = null,
        int lineCount = 2)
    {
        var entry = Guid.NewGuid();
        var counter = advanceCounter
            ? $"""
              INSERT INTO ledger.entry_sequence (tenant_id, company_id, fiscal_year, last_number)
              VALUES ({books.TenantId}, {books.CompanyId}, 2026, 1)
              ON CONFLICT (tenant_id, company_id, fiscal_year)
              DO UPDATE SET last_number = entry_sequence.last_number + 1;
              """
            : "";

        return $"""
            BEGIN;
            {counter}
            INSERT INTO ledger.journal_entry (id, tenant_id, company_id, public_id, number, fiscal_year,
              entry_date, memo, currency, posted_at, reverses_entry_id, line_count)
            VALUES ('{entry}', {books.TenantId}, {books.CompanyId}, 'je_raw{Guid.NewGuid().ToString("N")[..8]}',
              {number}, {fiscalYear ?? 2026}, '2026-09-25', 'raw', 'USD', now(), {(reverses is null ? "NULL" : $"'{reverses}'")},
              {lineCount});
            INSERT INTO ledger.journal_line (id, tenant_id, company_id, entry_id, account_id, line_number, amount_minor)
            VALUES ('{Guid.NewGuid()}', {books.TenantId}, {books.CompanyId}, '{entry}', '{line1.Account}', 1, {line1.Amount}),
                   ('{Guid.NewGuid()}', {books.TenantId}, {companyOfLine2 ?? books.CompanyId}, '{entry}', '{line2.Account}', 2, {line2.Amount});
            COMMIT;
            """;
    }

    private async Task<(Books Books, Guid Cash, Guid Sales)> BooksWithAccountsAsync()
    {
        var books = await NewBooksAsync();
        var cash = await AccountIdAsync(books, await AccountAsync(books, "1000", "asset"));
        var sales = await AccountIdAsync(books, await AccountAsync(books, "4000", "revenue"));
        return (books, cash, sales);
    }

    [Fact]
    public async Task A_correct_raw_entry_is_accepted_so_the_refusals_below_are_about_their_one_change()
        => await fixture.ExecuteAsAsync("ap_ledger_rt", await ValidRawEntryAsync());

    private async Task<string> ValidRawEntryAsync()
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();
        return RawEntry(books, number: 1, (cash, 100), (sales, -100));
    }

    [Theory]
    [InlineData(3, 4, "cannot be added")] // beyond the declared line count
    [InlineData(1, 2, "duplicate key")]   // inside it — every slot is already taken
    public async Task A_line_cannot_be_added_to_an_entry_already_posted(int first, int second, string refusal)
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();
        await fixture.ExecuteAsAsync("ap_ledger_rt", RawEntry(books, 1, (cash, 100), (sales, -100)));

        // A balanced PAIR, so only the seal can refuse it — the balance rule would let it through.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt", $"""
            BEGIN;
            INSERT INTO ledger.journal_line (id, tenant_id, company_id, entry_id, account_id, line_number, amount_minor)
            SELECT gen_random_uuid(), tenant_id, company_id, id, '{cash}'::uuid, {first}, 500 FROM ledger.journal_entry WHERE tenant_id = {books.TenantId}
            UNION ALL
            SELECT gen_random_uuid(), tenant_id, company_id, id, '{sales}'::uuid, {second}, -500 FROM ledger.journal_entry WHERE tenant_id = {books.TenantId};
            COMMIT;
            """));

        Assert.Contains(refusal, ex.MessageText);
    }

    [Fact]
    public async Task An_entry_short_of_its_declared_lines_is_refused()
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();

        // Declares three, writes two: a free slot would be a way to add a line later.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            RawEntry(books, 1, (cash, 100), (sales, -100), lineCount: 3)));

        Assert.Contains("declares 3 lines but has 2", ex.MessageText);
    }

    [Fact]
    public async Task Lines_cannot_be_written_before_their_entry()
    {
        // Regression (Codex, third round): a data-modifying CTE inserts the lines first, numbered
        // 3 and 4 against a declared count of 2. With the header missing when the lines arrived, the
        // range check was skipped and the foreign key passed at the end of the statement — leaving
        // slots 1 and 2 free for a later, unbalancing line.
        var (books, cash, sales) = await BooksWithAccountsAsync();
        var entry = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt", $"""
            BEGIN;
            INSERT INTO ledger.entry_sequence (tenant_id, company_id, fiscal_year, last_number)
            VALUES ({books.TenantId}, {books.CompanyId}, 2026, 1);
            WITH lines AS (
              INSERT INTO ledger.journal_line (id, tenant_id, company_id, entry_id, account_id, line_number, amount_minor)
              VALUES (gen_random_uuid(), {books.TenantId}, {books.CompanyId}, '{entry}', '{cash}', 3, 100),
                     (gen_random_uuid(), {books.TenantId}, {books.CompanyId}, '{entry}', '{sales}', 4, -100)
              RETURNING 1)
            INSERT INTO ledger.journal_entry (id, tenant_id, company_id, public_id, number, fiscal_year,
              entry_date, memo, currency, posted_at, line_count)
            SELECT '{entry}', {books.TenantId}, {books.CompanyId}, 'je_cte{Guid.NewGuid().ToString("N")[..8]}',
              1, 2026, '2026-09-25', 'cte', 'USD', now(), count(*) FROM lines;
            COMMIT;
            """));

        Assert.Contains("cannot be written before its journal entry", ex.MessageText);
        Assert.Empty(await NumbersAsync(books));
    }

    [Fact]
    public async Task An_entry_with_no_lines_at_all_is_refused()
    {
        // The case no line trigger could catch: there is no line to fire one.
        var (books, cash, sales) = await BooksWithAccountsAsync();
        var sql = RawEntry(books, 1, (cash, 100), (sales, -100));
        var noLines = sql[..sql.IndexOf("INSERT INTO ledger.journal_line", StringComparison.Ordinal)] + "COMMIT;";

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt", noLines));

        Assert.Contains("declares 2 lines but has 0", ex.MessageText);
    }

    [Fact]
    public async Task An_entry_written_across_a_savepoint_is_still_accepted()
    {
        // The seal is a count, not "same (sub)transaction": a header and its lines split by a
        // savepoint are one legitimate post.
        var (books, cash, sales) = await BooksWithAccountsAsync();
        var sql = RawEntry(books, 1, (cash, 100), (sales, -100))
            .Replace("INSERT INTO ledger.journal_line", "SAVEPOINT lines; INSERT INTO ledger.journal_line")
            .Replace("COMMIT;", "RELEASE SAVEPOINT lines; COMMIT;");

        await fixture.ExecuteAsAsync("ap_ledger_rt", sql);
        Assert.Equal([1], await NumbersAsync(books));
    }

    [Fact]
    public async Task A_line_cannot_use_another_companys_account()
    {
        var (books, cash, _) = await BooksWithAccountsAsync();
        var (otherCompany, otherPublicId) = await NewCompanyAsync(books.TenantId);
        var elsewhere = await AccountIdAsync(otherCompany, await AccountAsync(otherCompany, "4000", "revenue", otherPublicId));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            RawEntry(books, 1, (cash, 100), (elsewhere, -100))));

        Assert.Equal("fk_journal_line_account_tenant_id_company_id_account_id", ex.ConstraintName);
    }

    [Fact]
    public async Task An_account_cannot_move_to_another_company_once_posted_to()
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();
        await fixture.ExecuteAsAsync("ap_ledger_rt", RawEntry(books, 1, (cash, 100), (sales, -100)));
        var (otherCompany, _) = await NewCompanyAsync(books.TenantId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            $"UPDATE ledger.account SET company_id = {otherCompany.CompanyId} WHERE id = '{cash}'"));

        Assert.Equal("23503", ex.SqlState);
    }

    [Theory]
    [InlineData(true)]  // a series exists at 1, and the entry claims 5
    [InlineData(false)] // no series at all, and the entry claims 1
    public async Task An_entry_number_must_have_been_issued(bool seriesExists)
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();
        if (seriesExists) await fixture.ExecuteAsAsync("ap_ledger_rt", RawEntry(books, 1, (cash, 100), (sales, -100)));

        // The counter is NOT advanced, so the only rule this breaks is "issued before used".
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            RawEntry(books, seriesExists ? 5 : 1, (cash, 100), (sales, -100), advanceCounter: false)));

        Assert.Contains("was never issued", ex.MessageText);
    }

    [Fact]
    public async Task The_counter_cannot_issue_a_number_that_no_entry_uses()
    {
        var books = await NewBooksAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt", $"""
            INSERT INTO ledger.entry_sequence (tenant_id, company_id, fiscal_year, last_number)
            VALUES ({books.TenantId}, {books.CompanyId}, 2026, 1)
            """));

        Assert.Contains("issued but not used", ex.MessageText);
    }

    [Fact]
    public async Task The_counter_cannot_skip_numbers_or_be_removed()
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();
        await fixture.ExecuteAsAsync("ap_ledger_rt", RawEntry(books, 1, (cash, 100), (sales, -100)));

        var skip = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            $"UPDATE ledger.entry_sequence SET last_number = 5 WHERE tenant_id = {books.TenantId}"));
        Assert.Contains("advances by exactly one", skip.MessageText);

        Assert.Equal("42501", await fixture.TryAsAsync("ap_ledger_rt",
            $"DELETE FROM ledger.entry_sequence WHERE tenant_id = {books.TenantId}"));
    }

    [Fact]
    public async Task The_fiscal_year_is_the_year_of_the_entry_date()
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            RawEntry(books, 1, (cash, 100), (sales, -100), fiscalYear: 2025)));

        Assert.Equal("ck_journal_entry_fiscal_year", ex.ConstraintName);
    }

    [Fact]
    public async Task A_forged_reversal_that_does_not_mirror_its_original_is_refused()
    {
        var (books, cash, sales) = await BooksWithAccountsAsync();
        var original = EntryId(await PostAsync(books, (await PublicIdOf(books, cash), 100), (await PublicIdOf(books, sales), -100)));
        var originalId = await EntryGuidAsync(books, original);

        // Balanced, same accounts, but +1/-1 against an original of +100/-100.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt",
            RawEntry(books, 2, (cash, -1), (sales, 1), reverses: originalId.ToString())));
        Assert.Contains("does not exactly negate", ex.MessageText);

        // And the genuine reversal is still possible afterwards: the forgery took no slot.
        Assert.True((await ReverseAsync(books, original)).Succeeded);
    }

    [Fact]
    public async Task Forcing_the_reversal_check_early_cannot_let_a_later_line_through()
    {
        // Regression (Codex re-review): the mirror check ran only when the reversal row was inserted.
        // Forced to run early with SET CONSTRAINTS, it passed — and a balanced pair appended after it
        // escaped. Every line insert now queues the check again.
        var (books, cash, sales) = await BooksWithAccountsAsync();
        var original = EntryId(await PostAsync(books, (await PublicIdOf(books, cash), 100), (await PublicIdOf(books, sales), -100)));
        var originalId = await EntryGuidAsync(books, original);

        var sql = RawEntry(books, 2, (cash, -100), (sales, 100), reverses: originalId.ToString(), lineCount: 4)
            .Replace("COMMIT;", $"""
                SET CONSTRAINTS ledger.journal_entry_reversal_mirrors_original IMMEDIATE;
                INSERT INTO ledger.journal_line (id, tenant_id, company_id, entry_id, account_id, line_number, amount_minor)
                SELECT gen_random_uuid(), tenant_id, company_id, id, '{cash}'::uuid, 3, 7 FROM ledger.journal_entry
                 WHERE tenant_id = {books.TenantId} AND reverses_entry_id = '{originalId}'
                UNION ALL
                SELECT gen_random_uuid(), tenant_id, company_id, id, '{sales}'::uuid, 4, -7 FROM ledger.journal_entry
                 WHERE tenant_id = {books.TenantId} AND reverses_entry_id = '{originalId}';
                COMMIT;
                """);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsAsync("ap_ledger_rt", sql));
        Assert.Contains("does not exactly negate", ex.MessageText);
    }

    private async Task<string> PublicIdOf(Books books, Guid accountId)
    {
        await using var db = Open(books);
        return (await db.Accounts.SingleAsync(a => a.Id == accountId)).PublicId;
    }

    private async Task<Guid> EntryGuidAsync(Books books, string publicId)
    {
        await using var db = Open(books);
        return (await db.Entries.SingleAsync(e => e.PublicId == publicId)).Id;
    }

    /// <summary>A second company in an existing tenant, with its public id for naming it in requests.</summary>
    private async Task<(Books Books, string PublicId)> NewCompanyAsync(int tenantId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var publicId = Ids.PublicId.New("co").ToString();
        await using var command = new NpgsqlCommand(
            $"INSERT INTO core.company (tenant_id, public_id, name, active) VALUES ({tenantId}, " +
            $"'{publicId}', 'Second Ltd', true) RETURNING id", connection);
        return (new Books(tenantId, (int)(await command.ExecuteScalarAsync())!), publicId);
    }

    [Fact]
    public async Task A_retried_post_returns_the_first_entry_and_posts_nothing_more()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");
        var key = Guid.NewGuid().ToString();

        // Three at once with one key — as a client retrying after timeouts would send them.
        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => PostWithKeyAsync(books, key, (cash, 100), (sales, -100))));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
        Assert.Single(results.Select(EntryId).Distinct());
        Assert.Equal([1], await NumbersAsync(books));

        var reused = await PostWithKeyAsync(books, key, (cash, 999), (sales, -999));
        Assert.Equal(LedgerProblems.IdempotencyKeyReused, reused.ProblemCode);
    }

    [Fact]
    public async Task Balances_beyond_64_bits_are_totalled_exactly()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");

        // Two posts that are each fine, whose sum on each account exceeds long.MaxValue.
        Assert.True((await PostAsync(books, (cash, long.MaxValue), (sales, -long.MaxValue))).Succeeded);
        Assert.True((await PostAsync(books, (cash, long.MaxValue), (sales, -long.MaxValue))).Succeeded);

        await using var db = Open(books);
        var result = await new GetTrialBalanceFeature.GetTrialBalanceQueryHandler(Service(books, db), TimeProvider.System)
            .Handle(Caller(books), Day, null);

        var usd = Assert.Single(((GetTrialBalanceFeature.TrialBalanceModel)result.Value!).Currencies);
        Assert.Equal(2m * long.MaxValue, usd.TotalDebitMinor);
        Assert.Equal(usd.TotalDebitMinor, usd.TotalCreditMinor);
    }

    [Fact]
    public async Task Posting_is_audited_against_the_caller()
    {
        var books = await NewBooksAsync();
        var cash = await AccountAsync(books, "1000", "asset");
        var sales = await AccountAsync(books, "4000", "revenue");
        var entryId = EntryId(await PostAsync(books, (cash, 100), (sales, -100)));

        await using var db = Open(books);
        var row = await db.AuditLog.SingleAsync(r => r.EntityId == entryId);
        Assert.Equal(AuditAction.Created, row.Action);
        Assert.Equal(Ada, row.ActorId);
        Assert.Equal("journal_entry", row.EntityType);
    }
}
