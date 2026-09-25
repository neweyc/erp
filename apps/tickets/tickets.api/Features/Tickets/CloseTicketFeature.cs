using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tickets.Data;
using AppPlatform.Tickets.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.Tickets.Features.Tickets;

public static class CloseTicketFeature
{
    public class CloseTicketCommandHandler(
        ITicketService tickets, IOutbox outbox, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(
            Caller caller, string ticketPublicId, CancellationToken ct = default)
        {
            if (!PublicId.TryParse(ticketPublicId, "tkt", out _))
                return CommandResult.NotFound(TicketProblems.NotFound, "That is not a ticket id.");

            var ticket = await tickets.FindByPublicIdAsync(ticketPublicId, ct);
            if (ticket is null)
                return CommandResult.NotFound(TicketProblems.NotFound, "No such ticket.");

            if (ticket.Status == TicketStatus.Closed)
            {
                // Reported rather than treated as success: closing an already-closed ticket
                // would otherwise move ClosedAt and emit a second event, rewriting when it
                // actually happened.
                return CommandResult.Conflict(TicketProblems.AlreadyClosed, "This ticket is already closed.");
            }

            ticket.Status = TicketStatus.Closed;
            ticket.ClosedAt = clock.GetUtcNow();
            ticket.Version++;

            outbox.AddEvent("ticket", ticket.PublicId, ticket.Version, "ticket.closed",
                $$"""{"ticketId":"{{ticket.PublicId}}"}""");

            try
            {
                await tickets.SaveAsync(ct);
            }
            catch (Exception ex) when (ConcurrencyConflict.Matches(ex))
            {
                // Someone else changed this ticket between our read and our write. A stable 409
                // tells the caller to re-read and retry; without it this escapes as a 500
                // naming a database constraint, which says nothing the caller can act on.
                return CommandResult.Conflict(
                    TicketProblems.ConcurrentChange,
                    "This ticket was changed by someone else. Reload and try again.");
            }

            return CommandResult.Ok(new { ticketId = ticket.PublicId, status = ticket.Status.ToString() });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/tickets/v1/tickets/{ticketId}/close",
            async (
                string ticketId,
                [FromServices] ICallerContext callerContext,
                [FromServices] ITicketService tickets,
                [FromServices] IOutbox outbox,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new CloseTicketCommandHandler(tickets, outbox, clock);
                return (await handler.Handle(callerContext.Require(), ticketId, ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Tickets);
    }
}
