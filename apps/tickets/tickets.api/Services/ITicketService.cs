using AppPlatform.Tickets.Data;

namespace AppPlatform.Tickets.Services;

public interface ITicketService
{
    Task<List<Ticket>> ListAsync(bool includeClosed, CancellationToken ct = default);
    Task<Ticket?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);
    void Add(Ticket ticket);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IEmployeeLookup
{
    /// <summary>
    /// Resolves an employee through the published view, under the tenant filter.
    ///
    /// Returning null is the ONLY defence against a ticket referencing another tenant's
    /// employee: PostgreSQL cannot key to a view, so there is no foreign key to fall back on.
    /// Resolve, never trust — an id in a request body is user input.
    /// </summary>
    Task<PublishedEmployee?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);
}
