using AppPlatform.Ledger.Data;
using AppPlatform.Ledger.Features;
using AppPlatform.Ledger.Features.Accounts;
using AppPlatform.Ledger.Features.Journal;
using AppPlatform.Ledger.Services;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AppPlatform.Ledger.Tests;

/// <summary>
/// Handler rules, against a mocked service. The database-level guarantees — the balance trigger,
/// gapless numbering under concurrency, append-only grants — are in privileges.tests/LedgerTests,
/// because only real PostgreSQL can show them failing.
/// </summary>
public class LedgerTests
{
    private readonly Mock<ILedgerService> _ledger = new();
    private readonly Mock<IDbContextTransaction> _transaction = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly FakeTimeProvider _clock = new(Fake.Now);
    private readonly List<JournalEntry> _posted = [];

    public LedgerTests()
    {
        _ledger.Setup(l => l.ActiveCompaniesAsync(default)).ReturnsAsync([Fake.Company()]);
        _ledger.Setup(l => l.BeginTransactionAsync(default)).ReturnsAsync(_transaction.Object);
        _ledger.Setup(l => l.NextEntryNumberAsync(It.IsAny<int>(), It.IsAny<int>(), default)).ReturnsAsync(7);
        _ledger.Setup(l => l.AddEntry(It.IsAny<JournalEntry>())).Callback<JournalEntry>(_posted.Add);
    }

    private void Known(params Account[] accounts)
        => _ledger.Setup(l => l.FindAccountsAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync((IEnumerable<string> ids, CancellationToken _) =>
                [.. accounts.Where(a => ids.Contains(a.PublicId))]);

    private Task<Api.CommandResult> Post(params (Account Account, long Amount)[] lines)
        => Post("USD", Fake.Today, lines);

    private Task<Api.CommandResult> Post(string currency, DateOnly? date, params (Account Account, long Amount)[] lines)
        => PostWithKey("key-1", currency, date, lines);

    private Task<Api.CommandResult> PostWithKey(
        string? key, string currency, DateOnly? date, params (Account Account, long Amount)[] lines)
        => new PostJournalEntryFeature.PostJournalEntryCommandHandler(_ledger.Object, _outbox, _clock)
            .Handle(Fake.Caller(), new(date, "Sale", currency,
                [.. lines.Select(l => new PostJournalEntryFeature.LineCommand(l.Account.PublicId, l.Amount, null))],
                key));

    // --- the balance rule -------------------------------------------------------------------------

    [Theory]
    [InlineData(new long[] { 100 }, false)]                  // one line: nothing to balance against
    [InlineData(new long[] { 100, 0, -100 }, false)]         // a zero line
    [InlineData(new long[] { 100, -99 }, false)]             // off by a cent
    [InlineData(new long[] { 100, -60, -40 }, true)]
    // Two maximum debits overflow a long and wrap to -2, so a 64-bit sum would call this balanced.
    [InlineData(new long[] { long.MaxValue, long.MaxValue, 2 }, false)]
    // The one value with no negation: an entry holding it could never be reversed.
    [InlineData(new long[] { long.MinValue, long.MaxValue, 1 }, false)]
    // And a genuinely balanced entry whose running total passes long.MaxValue is still balanced.
    [InlineData(new long[] { long.MaxValue, 1, -long.MaxValue, -1 }, true)]
    public void Lines_must_be_two_or_more_non_zero_and_sum_to_zero(long[] amounts, bool postable)
    {
        var lines = amounts.Select(a => new Posting.Line(Guid.NewGuid(), a, null)).ToList();

        Assert.Equal(postable, Posting.Problem(lines) is null);
    }

    [Fact]
    public async Task An_unbalanced_entry_is_refused_before_anything_is_numbered()
    {
        var cash = Fake.Account("1000");
        var sales = Fake.Account("4000", AccountType.Revenue);
        Known(cash, sales);

        var result = await Post((cash, 100), (sales, -99));

        Assert.Equal(LedgerProblems.Unbalanced, result.ProblemCode);
        _ledger.Verify(l => l.NextEntryNumberAsync(It.IsAny<int>(), It.IsAny<int>(), default), Times.Never);
    }

    // --- resolve, never trust ---------------------------------------------------------------------

    [Fact]
    public async Task An_account_that_does_not_resolve_is_refused()
    {
        var cash = Fake.Account("1000");
        var elsewhere = Fake.Account("4000"); // not returned by the filtered lookup: another tenant's
        Known(cash);

        var result = await Post((cash, 100), (elsewhere, -100));

        Assert.Equal(LedgerProblems.AccountNotFound, result.ProblemCode);
        Assert.Empty(_posted);
    }

    [Fact]
    public async Task An_id_of_the_wrong_kind_is_reported_as_that_rather_than_looked_up()
    {
        var result = await new PostJournalEntryFeature.PostJournalEntryCommandHandler(_ledger.Object, _outbox, _clock)
            .Handle(Fake.Caller(), new(Fake.Today, "Sale", "USD",
                [new(Ids.PublicId.New("je").ToString(), 100, null), new(Ids.PublicId.New("je").ToString(), -100, null)],
                "key-1"));

        Assert.Equal(LedgerProblems.AccountNotFound, result.ProblemCode);
        _ledger.Verify(l => l.FindAccountsAsync(It.IsAny<IEnumerable<string>>(), default), Times.Never);
    }

    [Fact]
    public async Task One_entry_cannot_touch_two_companies_books()
    {
        var ours = Fake.Account("1000", companyId: 1);
        var theirs = Fake.Account("4000", AccountType.Revenue, companyId: 2);
        Known(ours, theirs);

        var result = await Post((ours, 100), (theirs, -100));

        Assert.Equal(LedgerProblems.AccountsSpanCompanies, result.ProblemCode);
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDX")]
    public async Task Currency_must_be_a_three_letter_upper_case_code(string currency)
    {
        var cash = Fake.Account("1000");
        var sales = Fake.Account("4000", AccountType.Revenue);
        Known(cash, sales);

        var result = await Post(currency, Fake.Today, (cash, 100), (sales, -100));

        Assert.Equal(LedgerProblems.ValidationFailed, result.ProblemCode);
    }

    // --- posting ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_balanced_entry_is_numbered_written_evented_and_committed()
    {
        var cash = Fake.Account("1000");
        var sales = Fake.Account("4000", AccountType.Revenue);
        Known(cash, sales);

        var result = await Post((cash, 12_50), (sales, -12_50));

        Assert.True(result.Succeeded, result.Message);
        var entry = Assert.Single(_posted);
        Assert.Equal(7, entry.Number);
        Assert.Equal(2026, entry.FiscalYear);
        Assert.Equal([12_50L, -12_50L], entry.Lines.OrderBy(l => l.LineNumber).Select(l => l.AmountMinor));
        Assert.Equal([cash.Id, sales.Id], entry.Lines.OrderBy(l => l.LineNumber).Select(l => l.AccountId));

        Assert.Equal("journal_entry.posted", Assert.Single(_outbox.Events).EventType);
        _transaction.Verify(t => t.CommitAsync(default), Times.Once);
    }

    // --- idempotency ------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_post_without_an_idempotency_key_is_refused(string? key)
    {
        var cash = Fake.Account("1000");
        var sales = Fake.Account("4000", AccountType.Revenue);
        Known(cash, sales);

        var result = await PostWithKey(key, "USD", Fake.Today, (cash, 100), (sales, -100));

        Assert.Equal(LedgerProblems.ValidationFailed, result.ProblemCode);
    }

    [Fact]
    public async Task A_retry_with_the_same_key_and_request_returns_the_first_entry_and_posts_nothing()
    {
        var cash = Fake.Account("1000");
        var sales = Fake.Account("4000", AccountType.Revenue);
        Known(cash, sales);
        await Post((cash, 100), (sales, -100));
        var first = Assert.Single(_posted);
        _ledger.Setup(l => l.FindEntryByIdempotencyKeyAsync("key-1", default)).ReturnsAsync(first);

        var retry = await Post((cash, 100), (sales, -100));

        Assert.True(retry.Succeeded, retry.Message);
        Assert.Single(_posted);
        Assert.Equal(true, retry.Value!.GetType().GetProperty("replayed")!.GetValue(retry.Value));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_entry_is_refused()
    {
        var cash = Fake.Account("1000");
        var sales = Fake.Account("4000", AccountType.Revenue);
        Known(cash, sales);
        await Post((cash, 100), (sales, -100));
        _ledger.Setup(l => l.FindEntryByIdempotencyKeyAsync("key-1", default)).ReturnsAsync(_posted[0]);

        var different = await Post((cash, 200), (sales, -200));

        Assert.Equal(LedgerProblems.IdempotencyKeyReused, different.ProblemCode);
        Assert.Single(_posted);
    }

    // --- reversal ---------------------------------------------------------------------------------

    private JournalEntry Original(Guid? reverses = null)
    {
        var cash = Fake.Account("1000");
        var entry = new JournalEntry
        {
            PublicId = Ids.PublicId.New("je").ToString(),
            CompanyId = 1, Number = 3, FiscalYear = 2026, EntryDate = Fake.Today.AddDays(-5),
            Memo = "Sale", Currency = "USD", ReversesEntryId = reverses,
            Lines =
            [
                new() { AccountId = cash.Id, LineNumber = 1, AmountMinor = 500 },
                new() { AccountId = Guid.NewGuid(), LineNumber = 2, AmountMinor = -500 },
            ],
        };

        _ledger.Setup(l => l.FindEntryWithLinesAsync(entry.PublicId, default)).ReturnsAsync(entry);
        return entry;
    }

    private Task<Api.CommandResult> Reverse(JournalEntry original, DateOnly? date = null)
        => new ReverseJournalEntryFeature.ReverseJournalEntryCommandHandler(_ledger.Object, _outbox, _clock)
            .Handle(Fake.Caller(), original.PublicId, new(date, null));

    [Fact]
    public async Task A_reversal_negates_every_line_and_points_at_the_original()
    {
        var original = Original();

        var result = await Reverse(original);

        Assert.True(result.Succeeded, result.Message);
        var reversal = Assert.Single(_posted);
        Assert.Equal(original.Id, reversal.ReversesEntryId);
        Assert.Equal([-500L, 500L], reversal.Lines.OrderBy(l => l.LineNumber).Select(l => l.AmountMinor));
        // Dated when the correction is made, not back in the original's period.
        Assert.Equal(Fake.Today, reversal.EntryDate);
    }

    [Fact]
    public async Task An_entry_already_reversed_is_refused()
    {
        var original = Original();
        _ledger.Setup(l => l.IsReversedAsync(original.Id, default)).ReturnsAsync(true);

        Assert.Equal(LedgerProblems.AlreadyReversed, (await Reverse(original)).ProblemCode);
        Assert.Empty(_posted);
    }

    [Fact]
    public async Task A_reversal_is_not_itself_reversible()
        => Assert.Equal(LedgerProblems.CannotReverseAReversal,
            (await Reverse(Original(reverses: Guid.NewGuid()))).ProblemCode);

    [Fact]
    public async Task A_reversal_cannot_be_dated_before_what_it_reverses()
    {
        var original = Original();

        var result = await Reverse(original, original.EntryDate.AddDays(-1));

        Assert.Equal(LedgerProblems.ValidationFailed, result.ProblemCode);
    }

    // --- accounts and companies -------------------------------------------------------------------

    private Task<Api.CommandResult> CreateAccount(string code, string type)
        => new CreateAccountFeature.CreateAccountCommandHandler(_ledger.Object, _outbox, _clock)
            .Handle(Fake.Caller(), new(code, "Cash", type, null));

    [Theory]
    [InlineData("asset,liability")] // Enum.TryParse would read this as Liability
    [InlineData("4")]               // or this as Expense
    [InlineData("bank")]
    [InlineData("")]
    public async Task An_account_type_must_be_one_of_the_five_by_name(string type)
        => Assert.Equal(LedgerProblems.ValidationFailed, (await CreateAccount("1000", type)).ProblemCode);

    [Theory]
    [InlineData("1000!")]
    [InlineData("-1000")]
    [InlineData("10 00")]
    [InlineData("123456789012345678901")]
    public async Task An_account_code_is_short_and_plain(string code)
        => Assert.Equal(LedgerProblems.ValidationFailed, (await CreateAccount(code, "asset")).ProblemCode);

    [Fact]
    public async Task An_account_is_created_in_the_only_company_when_none_is_named()
    {
        Account? added = null;
        _ledger.Setup(l => l.AddAccount(It.IsAny<Account>())).Callback<Account>(a => added = a);

        var result = await CreateAccount("1000", "ASSET");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(AccountType.Asset, added!.Type);
        Assert.Equal(1, added.CompanyId);
    }

    [Fact]
    public async Task With_several_companies_the_caller_must_say_which()
    {
        _ledger.Setup(l => l.ActiveCompaniesAsync(default)).ReturnsAsync([Fake.Company(1), Fake.Company(2)]);

        Assert.Equal(LedgerProblems.CompanyRequired, (await CreateAccount("1000", "asset")).ProblemCode);
    }
}
