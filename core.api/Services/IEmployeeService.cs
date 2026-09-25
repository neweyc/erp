using AppPlatform.Core.Data;

namespace AppPlatform.Core.Services;

/// <summary>
/// A thin EF wrapper. Handlers depend on this interface, never on the DbContext, so a handler's
/// rules are unit-testable without a database — which is where most of a feature's behaviour
/// actually lives.
/// </summary>
public interface IEmployeeService
{
    Task<List<Employee>> ListAsync(bool includeTerminated, CancellationToken ct = default);
    Task<Employee?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);
    Task<bool> EmailInUseAsync(string email, CancellationToken ct = default);
    Task<int> DefaultCompanyIdAsync(CancellationToken ct = default);
    void Add(Employee employee);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IUserService
{
    Task<bool> EmailInUseAsync(string email, CancellationToken ct = default);
    Task<User?> FindByEmployeeAsync(Guid employeeId, CancellationToken ct = default);
    void Add(User user);
}
