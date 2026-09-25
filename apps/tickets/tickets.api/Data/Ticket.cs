using AppPlatform.Audit;
using AppPlatform.Ids;
using AppPlatform.Tenancy;

namespace AppPlatform.Tickets.Data;

/// <summary>
/// Open and Closed are a FIXED enum, deliberately not tenant-configurable wording.
///
/// The rule carried from EMS: only hand over a list nothing branches on. Code counts open
/// tickets and routes on closure, so renaming a value would silently change behaviour. The
/// tenant gets to rename the LABEL; the value stays a code.
/// </summary>
public enum TicketStatus
{
    Open,
    Closed,
}

public class Ticket : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public string PublicId { get; set; } = "";

    public required string Title { get; set; }
    public string? Description { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    /// <summary>The employee this is assigned to, read from <c>core_v1</c>. Null means unassigned.</summary>
    public Guid? AssigneeEmployeeId { get; set; }

    /// <summary>
    /// The assignee's name AS IT WAS when the assignment was made.
    ///
    /// Not redundant with the id: a ticket closed in 2024 must still name who it was assigned
    /// to after that employee is deleted from core. Live join for the current state, snapshot
    /// for history — and the snapshot is what makes the join safe to fail.
    /// </summary>
    public string? AssigneeDisplayName { get; set; }

    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
