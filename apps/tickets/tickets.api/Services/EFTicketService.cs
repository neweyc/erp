using AppPlatform.Tickets.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tickets.Services;

public class EFTicketService(TicketsDbContext db) : ITicketService
{
    public Task<List<Ticket>> ListAsync(bool includeClosed, CancellationToken ct = default)
        => db.Tickets
            .Where(t => includeClosed || t.Status == TicketStatus.Open)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    // FirstOrDefaultAsync, never FindAsync: Find can be served from the change tracker and so
    // bypass the tenant query filter.
    public Task<Ticket?> FindByPublicIdAsync(string publicId, CancellationToken ct = default)
        => db.Tickets.FirstOrDefaultAsync(t => t.PublicId == publicId, ct);

    public void Add(Ticket ticket) => db.Tickets.Add(ticket);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public class EFEmployeeLookup(TicketsDbContext db) : IEmployeeLookup
{
    public Task<PublishedEmployee?> FindByPublicIdAsync(string publicId, CancellationToken ct = default)
        => db.Employees.FirstOrDefaultAsync(e => e.PublicId == publicId, ct);
}
