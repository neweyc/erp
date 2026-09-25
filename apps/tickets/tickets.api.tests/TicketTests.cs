using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Tickets.Data;
using AppPlatform.Tickets.Features.Tickets;
using AppPlatform.Tickets.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AppPlatform.Tickets.Tests;

public class TicketTests
{
    private readonly Mock<ITicketService> _tickets = new();
    private readonly Mock<IEmployeeLookup> _employees = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly FakeTimeProvider _clock = new(Fake.Now);

    private CreateTicketFeature.CreateTicketCommandHandler Create()
        => new(_tickets.Object, _employees.Object, _outbox, _clock);

    private AssignTicketFeature.AssignTicketCommandHandler Assign()
        => new(_tickets.Object, _employees.Object, _outbox);

    private CloseTicketFeature.CloseTicketCommandHandler Close()
        => new(_tickets.Object, _outbox, _clock);

    private Ticket Existing(TicketStatus status = TicketStatus.Open)
    {
        var ticket = Fake.Ticket(status);
        _tickets.Setup(t => t.FindByPublicIdAsync(ticket.PublicId, default)).ReturnsAsync(ticket);
        return ticket;
    }

    private PublishedEmployee Known(string status = "Active")
    {
        var employee = Fake.Employee(status);
        _employees.Setup(e => e.FindByPublicIdAsync(employee.PublicId, default)).ReturnsAsync(employee);
        return employee;
    }

    [Fact]
    public async Task A_ticket_is_created_with_a_public_id_and_an_event()
    {
        Ticket? added = null;
        _tickets.Setup(t => t.Add(It.IsAny<Ticket>())).Callback<Ticket>(t => added = t);

        var result = await Create().Handle(Fake.Caller(), new("Printer jammed", null, null));

        Assert.True(result.Succeeded);
        Assert.StartsWith("tkt_", added!.PublicId, StringComparison.Ordinal);
        Assert.Equal(TicketStatus.Open, added.Status);
        Assert.Equal("ticket.created", Assert.Single(_outbox.Events).EventType);
        _tickets.Verify(t => t.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task Assigning_stores_both_the_id_and_a_display_snapshot()
    {
        var ticket = Existing();
        var employee = Known();

        await Assign().Handle(Fake.Caller(), ticket.PublicId, new(employee.PublicId));

        // Both, not either. The id is the live link; the snapshot is what still names the
        // assignee after that employee is deleted from core.
        Assert.Equal(employee.Id, ticket.AssigneeEmployeeId);
        Assert.Equal("Ada Lovelace", ticket.AssigneeDisplayName);
    }

    [Fact]
    public async Task An_assignee_that_does_not_resolve_through_the_view_is_refused()
    {
        var ticket = Existing();

        // Resolve-never-trust. PostgreSQL cannot key to a view, so there is NO foreign key
        // behind this: the lookup returning null is the only thing stopping a ticket pointing
        // at another tenant's employee.
        var result = await Assign().Handle(
            Fake.Caller(), ticket.PublicId, new(PublicId.New("emp").ToString()));

        Assert.Equal(TicketProblems.AssigneeNotFound, result.ProblemCode);
        Assert.Null(ticket.AssigneeEmployeeId);
    }

    [Fact]
    public async Task An_employee_id_of_the_wrong_kind_is_refused_before_the_lookup()
    {
        var ticket = Existing();

        var result = await Assign().Handle(
            Fake.Caller(), ticket.PublicId, new(PublicId.New("tkt").ToString()));

        Assert.Equal(TicketProblems.AssigneeNotFound, result.ProblemCode);
        _employees.Verify(e => e.FindByPublicIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task A_terminated_employee_cannot_be_assigned()
    {
        var ticket = Existing();
        var employee = Known("Terminated");

        var result = await Assign().Handle(Fake.Caller(), ticket.PublicId, new(employee.PublicId));

        Assert.Equal(TicketProblems.AssigneeTerminated, result.ProblemCode);
    }

    [Fact]
    public async Task Unassigning_clears_the_snapshot_as_well_as_the_id()
    {
        var ticket = Existing();
        ticket.AssigneeEmployeeId = Guid.NewGuid();
        ticket.AssigneeDisplayName = "Ada Lovelace";

        await Assign().Handle(Fake.Caller(), ticket.PublicId, new(null));

        // A stale name beside a null id would show the ticket as assigned to someone it is not.
        Assert.Null(ticket.AssigneeEmployeeId);
        Assert.Null(ticket.AssigneeDisplayName);
    }

    [Fact]
    public async Task Reassigning_re_snapshots_the_name()
    {
        var ticket = Existing();
        ticket.AssigneeDisplayName = "Someone Else";
        var employee = Known();

        await Assign().Handle(Fake.Caller(), ticket.PublicId, new(employee.PublicId));

        // The snapshot records who it was assigned to at THIS moment, not at creation.
        Assert.Equal("Ada Lovelace", ticket.AssigneeDisplayName);
    }

    [Fact]
    public async Task Closing_sets_the_status_and_the_time()
    {
        var ticket = Existing();

        var result = await Close().Handle(Fake.Caller(), ticket.PublicId);

        Assert.True(result.Succeeded);
        Assert.Equal(TicketStatus.Closed, ticket.Status);
        Assert.Equal(Fake.Now, ticket.ClosedAt);
        Assert.Equal("ticket.closed", Assert.Single(_outbox.Events).EventType);
    }

    [Fact]
    public async Task A_closed_ticket_cannot_be_reassigned()
    {
        var ticket = Existing(TicketStatus.Closed);
        ticket.AssigneeDisplayName = "Ada Lovelace";
        var employee = Known();

        var result = await Assign().Handle(Fake.Caller(), ticket.PublicId, new(employee.PublicId));

        // The UI disables the control, but a script or a stale page would otherwise reassign a
        // closed ticket and emit an event for it.
        Assert.Equal(TicketProblems.AlreadyClosed, result.ProblemCode);
        Assert.Equal("Ada Lovelace", ticket.AssigneeDisplayName);
        Assert.Empty(_outbox.Events);
    }

    [Fact]
    public async Task A_closed_ticket_cannot_be_unassigned_either()
    {
        var ticket = Existing(TicketStatus.Closed);
        ticket.AssigneeEmployeeId = Guid.NewGuid();
        ticket.AssigneeDisplayName = "Ada Lovelace";

        var result = await Assign().Handle(Fake.Caller(), ticket.PublicId, new(null));

        // Clearing an assignee is still a change to a closed record.
        Assert.Equal(TicketProblems.AlreadyClosed, result.ProblemCode);
        Assert.Equal("Ada Lovelace", ticket.AssigneeDisplayName);
    }

    [Fact]
    public async Task Closing_an_already_closed_ticket_is_refused()
    {
        var ticket = Existing(TicketStatus.Closed);
        ticket.ClosedAt = Fake.Now.AddDays(-3);

        var result = await Close().Handle(Fake.Caller(), ticket.PublicId);

        // Treating it as success would move ClosedAt and emit a second event, rewriting when
        // the ticket actually closed.
        Assert.Equal(TicketProblems.AlreadyClosed, result.ProblemCode);
        Assert.Equal(Fake.Now.AddDays(-3), ticket.ClosedAt);
        Assert.Empty(_outbox.Events);
    }

    [Fact]
    public async Task Each_change_advances_the_version_so_a_consumer_can_detect_a_gap()
    {
        var ticket = Existing();
        var employee = Known();

        await Assign().Handle(Fake.Caller(), ticket.PublicId, new(employee.PublicId));
        await Close().Handle(Fake.Caller(), ticket.PublicId);

        Assert.Equal([2L, 3L], _outbox.Events.Select(e => e.AggregateVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_ticket_without_a_title_is_refused(string? title)
    {
        var result = await Create().Handle(Fake.Caller(), new(title, null, null));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Empty(_outbox.Events);
    }

    [Fact]
    public async Task An_over_long_title_is_refused_by_the_handler()
    {
        var result = await Create().Handle(Fake.Caller(), new(new string('a', 201), null, null));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task An_unknown_ticket_is_not_found()
        => Assert.Equal(CommandOutcome.NotFound,
            (await Close().Handle(Fake.Caller(), PublicId.New("tkt").ToString())).Outcome);
}
