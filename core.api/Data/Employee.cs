using AppPlatform.Audit;
using AppPlatform.Ids;
using AppPlatform.Tenancy;

namespace AppPlatform.Core.Data;

public enum EmployeeStatus
{
    Active,
    OnLeave,
    Terminated,
}

/// <summary>
/// A person who works here. Core's central record, and the one every app references.
///
/// **Terminated is not deleted.** Soft delete is for records created in error only; a
/// terminated employee keeps their history and stays visible to anything that references them
/// historically, while being excluded from active counts. Every new employee-facing query has
/// to decide explicitly how it treats terminated employees and say so.
/// </summary>
public class Employee : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public int CompanyId { get; set; }
    public string PublicId { get; set; } = "";

    public required string FirstName { get; set; }
    public required string LastName { get; set; }

    /// <summary>
    /// Plaintext on purpose: it is matched during invitation and searched in lists, and an
    /// encrypted column cannot be filtered in SQL. Sensitive fields that never need filtering
    /// (phone, address) get the encrypting converter instead.
    /// </summary>
    public string? Email { get; set; }

    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;

    /// <summary>
    /// Bumped on every change that emits an event, so a consumer can detect a gap. The
    /// aggregate owns its counter — the outbox does not invent one.
    /// </summary>
    public long Version { get; set; } = 1;

    /// <summary>Records created in error. Distinct from termination, which is a real status.</summary>
    public bool Deleted { get; set; }

    public string DisplayName => $"{FirstName} {LastName}".Trim();
}
