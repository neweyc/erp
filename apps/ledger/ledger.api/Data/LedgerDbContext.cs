using AppPlatform.Audit;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Ledger.Data;

public class LedgerDbContext(
    DbContextOptions<LedgerDbContext> options, ITenantProvider tenant, IAuditActor auditActor, TimeProvider clock)
    : AuditedDbContext(options, tenant, auditActor, clock)
{
    public const string Schema = "ledger";
    public const string PublishedSchema = "core_v1";

    protected override string AuditSchema => Schema;

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> Entries => Set<JournalEntry>();
    public DbSet<JournalLine> Lines => Set<JournalLine>();
    public DbSet<EntrySequence> Sequences => Set<EntrySequence>();
    public DbSet<PublishedCompany> Companies => Set<PublishedCompany>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("account", Schema, t =>
                // Enforced in the database as well as the handler: an account code is what a
                // person types, and "1000 " must not become a second account beside "1000".
                t.HasCheckConstraint("ck_account_code", "code ~ '^[0-9A-Za-z][0-9A-Za-z.-]{0,19}$'"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.HasPublicId("acct");

            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique();

            // Tenant AND company, so a line can key to it only within one company's books — and so
            // an account's company cannot be changed once anything has been posted to it.
            e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });

            // NO foreign key to the company: it lives behind a published view, and PostgreSQL
            // cannot key to a view. Resolve-never-trust in the handler is the defence.
        });

        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.ToTable("journal_entry", Schema, t =>
            {
                t.HasCheckConstraint("ck_journal_entry_currency", "currency ~ '^[A-Z]{3}$'");
                // The series an entry is numbered in is the year of its date — never a year a
                // caller chose — and numbers start at 1.
                t.HasCheckConstraint("ck_journal_entry_fiscal_year", "fiscal_year = extract(year from entry_date)");
                t.HasCheckConstraint("ck_journal_entry_number", "number > 0");
                t.HasCheckConstraint("ck_journal_entry_line_count", "line_count >= 2");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Memo).HasMaxLength(500);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.HasPublicId("je");

            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Property(x => x.RequestFingerprint).HasMaxLength(64);

            e.HasIndex(x => x.TenantId);
            e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });

            // One entry per idempotency key per tenant. The index, not a preflight read, is what
            // stops two concurrent retries both posting.
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");

            // The gapless series. Unique, so even a bug in allocation cannot issue one number twice.
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.FiscalYear, x.Number }).IsUnique();

            // A reversal points at what it reverses, carrying tenant and company, so it can only
            // reverse an entry in the same books. Unique: an entry is reversed at most once, and two
            // racing reversals cannot both commit.
            e.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ReversesEntryId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.ReversesEntryId })
                .IsUnique()
                .HasFilter("reverses_entry_id IS NOT NULL");

            e.HasMany(x => x.Lines)
                .WithOne()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EntryId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalLine>(e =>
        {
            e.ToTable("journal_line", Schema, t =>
            {
                // A line that moves nothing is a mistake, and would let a one-sided entry pass the
                // balance rule by padding it with a zero.
                t.HasCheckConstraint("ck_journal_line_amount_nonzero", "amount_minor <> 0");
                // The one bigint that cannot be negated: an entry holding it could never be reversed.
                t.HasCheckConstraint("ck_journal_line_amount_negatable", "amount_minor > -9223372036854775808");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Memo).HasMaxLength(500);

            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => new { x.TenantId, x.EntryId, x.LineNumber }).IsUnique();

            // Tenant- and company-carrying: a line cannot reference another tenant's account, nor
            // another company's, at the database. The same company as its entry, by the key above.
            e.HasOne<Account>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.AccountId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);

            // The trial balance reads every line of an account.
            e.HasIndex(x => new { x.TenantId, x.AccountId });
        });

        modelBuilder.Entity<EntrySequence>(e =>
        {
            e.ToTable("entry_sequence", Schema);
            e.HasKey(x => new { x.TenantId, x.CompanyId, x.FiscalYear });
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<PublishedCompany>(e =>
        {
            e.ToView("company", PublishedSchema);
            e.HasKey(x => x.Id);
        });

        modelBuilder.AddOutbox(Schema);

        base.OnModelCreating(modelBuilder);
    }
}
