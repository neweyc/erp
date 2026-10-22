using AppPlatform.Core.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppPlatform.Core.Services;

/// <summary>
/// The tenant's departments, job titles and locations. A thin EF wrapper, like the others.
///
/// Each list is small (tens of rows, not thousands), so the rules load a whole list and decide in
/// memory: a loop in the department tree, a duplicate name. That is only sound if no other write
/// changes the list meanwhile, which is what <see cref="LockCatalogsAsync"/> is for.
/// </summary>
public interface ICatalogService
{
    /// <summary>Every department in the tenant, inactive included, ordered by name.</summary>
    Task<List<Department>> ListDepartmentsAsync(CancellationToken ct = default);

    /// <summary>Every job title in the tenant, inactive included, ordered by name.</summary>
    Task<List<JobTitle>> ListJobTitlesAsync(CancellationToken ct = default);

    /// <summary>Every location in the tenant, inactive included, ordered by name.</summary>
    Task<List<Location>> ListLocationsAsync(CancellationToken ct = default);

    Task<Department?> FindDepartmentAsync(string publicId, CancellationToken ct = default);
    Task<JobTitle?> FindJobTitleAsync(string publicId, CancellationToken ct = default);
    Task<Location?> FindLocationAsync(string publicId, CancellationToken ct = default);

    void Add(Department department);
    void Add(JobTitle jobTitle);
    void Add(Location location);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes the tenant's catalog lock until the current transaction ends. Every catalog write takes
    /// it FIRST, before reading anything, so writes in one tenant run one after another and each sees
    /// what the previous one committed. Without it two moves made at once (A under B, B under A) each
    /// see no loop and together make one, and two creates of the same name both pass the check.
    /// One lock for all three lists: catalog edits are rare, and one key cannot be taken in two orders.
    /// Must run inside <see cref="BeginTransactionAsync"/>.
    /// </summary>
    Task LockCatalogsAsync(CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}
