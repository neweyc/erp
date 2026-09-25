using AppPlatform.Ledger.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppPlatform.Ledger.Services;

public interface ILedgerService
{
    /// <summary>The tenant's active companies, through the published view, under the tenant filter.</summary>
    Task<List<PublishedCompany>> ActiveCompaniesAsync(CancellationToken ct = default);

    Task<PublishedCompany?> FindCompanyAsync(string publicId, CancellationToken ct = default);

    Task<List<Account>> ListAccountsAsync(int? companyId, CancellationToken ct = default);

    /// <summary>Resolves account public ids under the tenant filter. Missing ids are simply absent.</summary>
    Task<List<Account>> FindAccountsAsync(IEnumerable<string> publicIds, CancellationToken ct = default);

    Task<JournalEntry?> FindEntryWithLinesAsync(string publicId, CancellationToken ct = default);

    Task<JournalEntry?> FindEntryByIdempotencyKeyAsync(string key, CancellationToken ct = default);

    Task<bool> IsReversedAsync(Guid entryId, CancellationToken ct = default);

    Task<List<JournalEntry>> ListEntriesWithLinesAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);

    /// <summary>Public ids of the given entries, under the tenant filter.</summary>
    Task<Dictionary<Guid, string>> FindEntryPublicIdsAsync(IEnumerable<Guid> entryIds, CancellationToken ct = default);

    /// <summary>Public ids of the entries that reverse the given ones, keyed by the reversed entry.</summary>
    Task<Dictionary<Guid, string>> FindReversalsOfAsync(IEnumerable<Guid> entryIds, CancellationToken ct = default);

    /// <summary>
    /// Signed balance per (account, currency) over a company's entries dated on or before
    /// <paramref name="asOf"/>, with the account's details — in ONE query, so an account created
    /// and posted to while this runs is either wholly in the result or wholly absent.
    /// </summary>
    Task<List<AccountBalanceRow>> TrialBalanceAsync(int companyId, DateOnly asOf, CancellationToken ct = default);

    /// <summary>
    /// Issues the next number in a (company, year) series. MUST be called inside the posting
    /// transaction: the number is gapless only because a failed post rolls the counter back too.
    /// </summary>
    Task<int> NextEntryNumberAsync(int companyId, int fiscalYear, CancellationToken ct = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>A company's books, under the tenant filter; null until its first account exists.</summary>
    Task<Books?> FindBooksAsync(int companyId, CancellationToken ct = default);

    Task<List<Books>> ListBooksAsync(CancellationToken ct = default);

    void AddBooks(Books books);

    void AddAccount(Account account);
    void AddEntry(JournalEntry entry);
    Task SaveAsync(CancellationToken ct = default);
}

/// <summary>
/// One account's balance in one currency. Decimal, not long: every line fits in 64 bits, but a
/// balance summed across many entries — or a column total across many accounts — need not.
/// </summary>
public record AccountBalanceRow(
    string AccountPublicId, string Code, string Name, AccountType Type, string Currency, decimal BalanceMinor);

public class EFLedgerService(LedgerDbContext db, ITenantProvider tenant) : ILedgerService
{
    public Task<List<PublishedCompany>> ActiveCompaniesAsync(CancellationToken ct = default)
        => db.Companies.Where(c => c.Active).OrderBy(c => c.Id).ToListAsync(ct);

    public Task<PublishedCompany?> FindCompanyAsync(string publicId, CancellationToken ct = default)
        => db.Companies.FirstOrDefaultAsync(c => c.PublicId == publicId, ct);

    public Task<List<Account>> ListAccountsAsync(int? companyId, CancellationToken ct = default)
        => db.Accounts
            .Where(a => companyId == null || a.CompanyId == companyId)
            .OrderBy(a => a.Code)
            .ToListAsync(ct);

    public Task<List<Account>> FindAccountsAsync(IEnumerable<string> publicIds, CancellationToken ct = default)
    {
        var ids = publicIds.Distinct().ToList();
        return db.Accounts.Where(a => ids.Contains(a.PublicId)).ToListAsync(ct);
    }

    public Task<JournalEntry?> FindEntryWithLinesAsync(string publicId, CancellationToken ct = default)
        => db.Entries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.PublicId == publicId, ct);

    public Task<JournalEntry?> FindEntryByIdempotencyKeyAsync(string key, CancellationToken ct = default)
        => db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == key, ct);

    public Task<bool> IsReversedAsync(Guid entryId, CancellationToken ct = default)
        => db.Entries.AnyAsync(e => e.ReversesEntryId == entryId, ct);

    public Task<List<JournalEntry>> ListEntriesWithLinesAsync(
        DateOnly? from, DateOnly? to, CancellationToken ct = default)
        => db.Entries
            .Include(e => e.Lines)
            .Where(e => (from == null || e.EntryDate >= from) && (to == null || e.EntryDate <= to))
            .OrderBy(e => e.EntryDate).ThenBy(e => e.FiscalYear).ThenBy(e => e.Number)
            .ToListAsync(ct);

    public Task<Dictionary<Guid, string>> FindEntryPublicIdsAsync(
        IEnumerable<Guid> entryIds, CancellationToken ct = default)
    {
        var ids = entryIds.Distinct().ToList();
        return db.Entries.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.PublicId, ct);
    }

    public async Task<Dictionary<Guid, string>> FindReversalsOfAsync(
        IEnumerable<Guid> entryIds, CancellationToken ct = default)
    {
        var ids = entryIds.Cast<Guid?>().ToList();
        return await db.Entries
            .Where(e => ids.Contains(e.ReversesEntryId))
            .ToDictionaryAsync(e => e.ReversesEntryId!.Value, e => e.PublicId, ct);
    }

    public Task<List<AccountBalanceRow>> TrialBalanceAsync(
        int companyId, DateOnly asOf, CancellationToken ct = default)
        // Summed in the database, as numeric. All three sets are tenant-filtered.
        => db.Lines
            .Join(db.Entries, l => l.EntryId, e => e.Id, (l, e) => new { Line = l, Entry = e })
            .Join(db.Accounts, x => x.Line.AccountId, a => a.Id, (x, a) => new { x.Line, x.Entry, Account = a })
            .Where(x => x.Entry.CompanyId == companyId && x.Entry.EntryDate <= asOf)
            .GroupBy(x => new { x.Account.PublicId, x.Account.Code, x.Account.Name, x.Account.Type, x.Entry.Currency })
            .Select(g => new AccountBalanceRow(
                g.Key.PublicId, g.Key.Code, g.Key.Name, g.Key.Type, g.Key.Currency,
                g.Sum(x => (decimal)x.Line.AmountMinor)))
            .ToListAsync(ct);

    public async Task<int> NextEntryNumberAsync(int companyId, int fiscalYear, CancellationToken ct = default)
    {
        // Raw SQL, which applies no query filter — so the tenant is bound explicitly, from the same
        // ambient scope the filter would have used.
        //
        // One statement that creates the series or advances it, and returns the new number. The row
        // it writes stays locked until the posting transaction ends: a concurrent post in the same
        // series waits here, and if this post rolls back the increment rolls back with it. That is
        // the whole of "gapless" — no number is issued that does not end up on a committed entry.
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot number an entry without a tenant scope.");

        var issued = await db.Database.SqlQuery<int>($"""
            INSERT INTO ledger.entry_sequence (tenant_id, company_id, fiscal_year, last_number)
            VALUES ({tenantId}, {companyId}, {fiscalYear}, 1)
            ON CONFLICT (tenant_id, company_id, fiscal_year)
            DO UPDATE SET last_number = entry_sequence.last_number + 1
            RETURNING last_number AS "Value"
            """).ToListAsync(ct);

        return issued.Single();
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => db.Database.BeginTransactionAsync(ct);

    public Task<Books?> FindBooksAsync(int companyId, CancellationToken ct = default)
        => db.Books.FirstOrDefaultAsync(b => b.CompanyId == companyId, ct);

    public Task<List<Books>> ListBooksAsync(CancellationToken ct = default)
        => db.Books.OrderBy(b => b.CompanyId).ToListAsync(ct);

    public void AddBooks(Books books) => db.Books.Add(books);

    public void AddAccount(Account account) => db.Accounts.Add(account);

    public void AddEntry(JournalEntry entry) => db.Entries.Add(entry);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
