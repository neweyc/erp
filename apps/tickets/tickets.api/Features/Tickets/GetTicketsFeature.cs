using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Entitlements;
using AppPlatform.Tickets.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Tickets.Features.Tickets;

public static class GetTicketsFeature
{
    public record TicketModel(
        string TicketId, string Title, string Status,
        string? AssigneeEmployeeId, string? AssigneeDisplayName,
        DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);

    public class GetTicketsQueryHandler(ITicketService tickets)
    {
        public async Task<CommandResult> Handle(
            Caller caller, bool includeClosed, CancellationToken ct = default)
        {
            var rows = await tickets.ListAsync(includeClosed, ct);

            return CommandResult.Ok(rows.Select(t => new TicketModel(
                t.PublicId, t.Title, t.Status.ToString(),
                // The employee's public id is NOT stored on the ticket — only the internal key
                // and the snapshot — so the list reports the name it recorded. A caller needing
                // the live employee asks core for it.
                null,
                t.AssigneeDisplayName,
                t.CreatedAt, t.ClosedAt)).ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/tickets/v1/tickets",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] ITicketService tickets,
                CancellationToken ct,
                bool? includeClosed = null) =>
            {
                var handler = new GetTicketsQueryHandler(tickets);
                return (await handler.Handle(callerContext.Require(), includeClosed ?? false, ct))
                    .CreateIResult();
            })
            .RequireAuthorization()
            .RequireApp(Apps.Tickets);
    }
}
