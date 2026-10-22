using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;

namespace AppPlatform.Core.Features.Employees;

/// <summary>
/// Where an employee sits: their department, job title and location, each optional. Shared by
/// adding and editing an employee, so the two can never place someone differently.
///
/// Each id arrives in a request body and is user input: it is parsed with its own prefix and looked
/// up through this tenant's filtered lists, so another tenant's department is simply not found
/// (the composite foreign keys refuse it again in the database). A retired item is refused unless
/// the employee already has it: deactivating a department must not force everyone in it to move
/// before their name can be corrected.
///
/// It does not take the catalog lock, so a department retired at the same moment as someone is
/// placed in it may end up held by them. That is a state the rules already allow (people keep a
/// retired department), so serializing employee edits behind catalog edits would buy nothing.
/// </summary>
public static class EmployeePlacement
{
    public record Placement(Guid? DepartmentId, Guid? JobTitleId, Guid? LocationId);

    /// <summary>The resolved placement, or the sentence saying which choice to fix.</summary>
    /// <param name="current">The employee being edited, or null when adding one.</param>
    public static async Task<(Placement? Placement, string? Problem)> ResolveAsync(
        string? departmentId, string? jobTitleId, string? locationId,
        Employee? current, ICatalogService catalogs, CancellationToken ct)
    {
        var (department, departmentProblem) = await ResolveOneAsync(
            departmentId, "dept", "department", current?.DepartmentId,
            id => catalogs.FindDepartmentAsync(id, ct), d => (d.Id, d.Name, d.Active));
        if (departmentProblem is not null) return (null, departmentProblem);

        var (jobTitle, jobTitleProblem) = await ResolveOneAsync(
            jobTitleId, "title", "job title", current?.JobTitleId,
            id => catalogs.FindJobTitleAsync(id, ct), t => (t.Id, t.Name, t.Active));
        if (jobTitleProblem is not null) return (null, jobTitleProblem);

        var (location, locationProblem) = await ResolveOneAsync(
            locationId, "loc", "location", current?.LocationId,
            id => catalogs.FindLocationAsync(id, ct), l => (l.Id, l.Name, l.Active));
        if (locationProblem is not null) return (null, locationProblem);

        return (new Placement(department, jobTitle, location), null);
    }

    private static async Task<(Guid? Id, string? Problem)> ResolveOneAsync<T>(
        string? publicId, string prefix, string noun, Guid? currentId,
        Func<string, Task<T?>> find, Func<T, (Guid Id, string Name, bool Active)> describe)
        where T : class
    {
        // Blank means "none": every placement is optional.
        if (string.IsNullOrWhiteSpace(publicId)) return (null, null);

        var trimmed = publicId.Trim();
        if (!PublicId.TryParse(trimmed, prefix, out _))
            return (null, $"That is not a {noun} id.");

        if (await find(trimmed) is not { } found)
            return (null, $"No such {noun}.");

        var (id, name, active) = describe(found);
        if (!active && id != currentId)
            return (null, $"{name} is no longer in use. Choose another {noun}.");

        return (id, null);
    }
}
