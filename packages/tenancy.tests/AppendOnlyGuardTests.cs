using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tenancy.Tests;

public class AppendOnlyGuardTests
{
    private readonly string _database = Guid.NewGuid().ToString();

    private async Task<Posting> Posted()
    {
        var (db, _) = Harness.Context(_database, tenantId: 1);
        await using var _db = db;
        var posting = new Posting { AmountMinor = 100 };
        db.Postings.Add(posting);
        await db.SaveChangesAsync();
        return posting;
    }

    [Fact]
    public async Task An_append_only_row_can_be_inserted()
    {
        await Posted();

        var (db, _) = Harness.Context(_database, tenantId: 1);
        await using var _db = db;
        Assert.Single(await db.Postings.ToListAsync());
    }

    [Fact]
    public async Task An_append_only_row_cannot_be_modified()
    {
        var posting = await Posted();

        var (db, _) = Harness.Context(_database, tenantId: 1);
        await using var _db = db;
        (await db.Postings.SingleAsync(p => p.Id == posting.Id)).AmountMinor = 999;

        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
        Assert.Throws<AppendOnlyViolationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task An_append_only_row_cannot_be_deleted_even_detached()
    {
        var posting = await Posted();

        var (db, _) = Harness.Context(_database, tenantId: 1);
        await using var _db = db;
        db.Postings.Remove(new Posting { Id = posting.Id, TenantId = 1 });

        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
    }
}
