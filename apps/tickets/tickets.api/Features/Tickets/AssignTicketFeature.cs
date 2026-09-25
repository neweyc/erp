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

public static class AssignTicketFeature
{
    public record AssignTicketCommand(string? EmployeeId);

    public class AssignTicketCommandHandler(
        ITicketService tickets,
        IEmployeeLookup employees,
        IOutbox outbox)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string ticketPublicId, AssignTicketCommand cmd,
            CancellationToken ct = default)
        {
            if (!PublicId.TryParse(ticketPublicId, "tkt", out _))
                return CommandResult.NotFound(TicketProblems.NotFound, "That is not a ticket id.");

            var ticket = await tickets.FindByPublicIdAsync(ticketPublicId, ct);
            if (ticket is null)
                return CommandResult.NotFound(TicketProblems.NotFound, "No such ticket.");

            if (ticket.Status == TicketStatus.Closed)
            {
                // Enforced here, not only by a disabled control. The UI disables the picker, but a
                // caller that is not the UI — a script, a stale page, a direct request — would
                // otherwise silently reassign a closed ticket and emit an event for it.
                return CommandResult.Conflict(
                    TicketProblems.AlreadyClosed,
                    "This ticket is closed. Reopen it before changing the assignee.");
            }

            if (string.IsNullOrWhiteSpace(cmd.EmployeeId))
            {
                // Unassigning clears the snapshot too. Leaving a stale name beside a null id
                // would show a ticket as assigned to someone it is not.
                ticket.AssigneeEmployeeId = null;
                ticket.AssigneeDisplayName = null;
            }
            else
            {
                var assignment = await AssignmentResolver.ResolveAsync(employees, cmd.EmployeeId, ct);
                if (assignment.Problem is { } problem) return problem;

                ticket.AssigneeEmployeeId = assignment.Employee!.Id;
                // Re-snapshotted on every assignment, so the name reflects who it was assigned
                // to at THAT moment — not who they were called when the ticket was created.
                ticket.AssigneeDisplayName = assignment.Employee.DisplayName;
            }

            ticket.Version++;

            outbox.AddEvent("ticket", ticket.PublicId, ticket.Version, "ticket.assigned",
                $$"""{"ticketId":"{{ticket.PublicId}}"}""");

            try
            {
                await tickets.SaveAsync(ct);
            }
            catch (Exception ex) when (DatabaseConflict.IsLostRace(ex))
            {
                // Someone else changed this ticket between our read and our write. A stable 409
                // tells the caller to re-read and retry; without it this escapes as a 500
                // naming a database constraint, which says nothing the caller can act on.
                return CommandResult.Conflict(
                    TicketProblems.ConcurrentChange,
                    "This ticket was changed by someone else. Reload and try again.");
            }

            return CommandResult.Ok(new
            {
                ticketId = ticket.PublicId,
                assigneeDisplayName = ticket.AssigneeDisplayName,
            });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/tickets/v1/tickets/{ticketId}/assignee",
            async (
                string ticketId,
                AssignTicketCommand cmd,
                [FromServices] ICallerContext callerContext,
                [FromServices] ITicketService tickets,
                [FromServices] IEmployeeLookup employees,
                [FromServices] IOutbox outbox,
                CancellationToken ct) =>
            {
                var handler = new AssignTicketCommandHandler(tickets, employees, outbox);
                return (await handler.Handle(callerContext.Require(), ticketId, cmd, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Tickets);
    }
}
