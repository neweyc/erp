using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tickets.Data;
using AppPlatform.Tickets.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tickets.Features.Tickets;

public static class CreateTicketFeature
{
    public record CreateTicketCommand(string? Title, string? Description, string? AssigneeEmployeeId);

    public class CreateTicketCommandHandler(
        ITicketService tickets,
        IEmployeeLookup employees,
        IOutbox outbox,
        TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Caller caller, CreateTicketCommand cmd, CancellationToken ct = default)
        {
            var title = cmd.Title?.Trim();

            if (string.IsNullOrWhiteSpace(title))
                return CommandResult.Invalid(TicketProblems.ValidationFailed, "A title is required.");

            if (title.Length > 200)
                return CommandResult.Invalid(TicketProblems.ValidationFailed, "Title is limited to 200 characters.");

            if (cmd.Description is { Length: > 4000 })
                return CommandResult.Invalid(TicketProblems.ValidationFailed, "Description is limited to 4000 characters.");

            var ticket = new Ticket
            {
                PublicId = PublicId.New("tkt").ToString(),
                Title = title,
                Description = cmd.Description?.Trim(),
                CreatedAt = clock.GetUtcNow(),
            };

            if (cmd.AssigneeEmployeeId is { Length: > 0 })
            {
                var assignment = await AssignmentResolver.ResolveAsync(employees, cmd.AssigneeEmployeeId, ct);
                if (assignment.Problem is { } problem) return problem;

                ticket.AssigneeEmployeeId = assignment.Employee!.Id;
                ticket.AssigneeDisplayName = assignment.Employee.DisplayName;
            }

            tickets.Add(ticket);

            outbox.AddEvent("ticket", ticket.PublicId, ticket.Version, "ticket.created",
                $$"""{"ticketId":"{{ticket.PublicId}}"}""");

            await tickets.SaveAsync(ct);

            return CommandResult.Ok(new { ticketId = ticket.PublicId });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/tickets/v1/tickets",
            async (
                CreateTicketCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ITicketService tickets,
                [FromServices] IEmployeeLookup employees,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new CreateTicketCommandHandler(tickets, employees, outbox, clock);
                return (await handler.Handle(callerContext.Require(), cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Tickets);
    }
}

public static class TicketProblems
{
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string AlreadyClosed = "already_closed";
    public const string AssigneeNotFound = "assignee_not_found";
    public const string AssigneeTerminated = "assignee_terminated";

    /// <summary>Someone else changed this ticket first. The caller should re-read and retry.</summary>
    public const string ConcurrentChange = "concurrent_change";
}

/// <summary>
/// Shared by create and assign, so the two cannot drift into resolving an assignee by different
/// rules — which is how one path ends up enforcing a check the other does not.
/// </summary>
public static class AssignmentResolver
{
    public record Resolution(PublishedEmployee? Employee, CommandResult? Problem);

    public static async Task<Resolution> ResolveAsync(
        IEmployeeLookup employees, string employeePublicId, CancellationToken ct)
    {
        // The prefix check first: a ticket id passed where an employee id was meant would
        // otherwise resolve to nothing and read as "no such employee".
        if (!PublicId.TryParse(employeePublicId, "emp", out _))
        {
            return new(null, CommandResult.Invalid(
                TicketProblems.AssigneeNotFound, "That is not an employee id."));
        }

        // Resolved THROUGH the tenant-filtered view. This is the only thing stopping a ticket
        // referencing another tenant's employee — there is no foreign key to a view.
        var employee = await employees.FindByPublicIdAsync(employeePublicId, ct);

        if (employee is null)
        {
            return new(null, CommandResult.Invalid(
                TicketProblems.AssigneeNotFound, "No such employee."));
        }

        if (employee.Status == "Terminated")
        {
            return new(null, CommandResult.Invalid(
                TicketProblems.AssigneeTerminated,
                "That employee has left. Assign someone who still works here."));
        }

        return new(employee, null);
    }
}
