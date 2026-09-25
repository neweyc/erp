using AppPlatform.Audit;

namespace AppPlatform.Ledger.Data;

/// <summary>
/// What an account holds, which decides its normal balance: assets and expenses normally carry a
/// debit, the rest a credit. Stored by name, so reordering the enum cannot change a stored account.
/// </summary>
public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

/// <summary>
/// One line of a company's chart of accounts.
///
/// The type is fixed at creation — there is no endpoint that changes it. Changing it would silently
/// move every posted balance from one side of the trial balance to the other.
/// </summary>
public class Account : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }

    /// <summary>The legal entity whose books this account belongs to (<c>core_v1.company.id</c>).</summary>
    public int CompanyId { get; set; }

    public string PublicId { get; set; } = "";

    /// <summary>The tenant's own account number, e.g. "1000". Unique within a company.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public AccountType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
