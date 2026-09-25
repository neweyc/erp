using Microsoft.AspNetCore.Routing;

namespace AppPlatform.Api;

/// <summary>
/// One endpoint. Implementations are discovered by reflection at startup, so a new feature
/// file is routable the moment it compiles — there is no registration list to forget to
/// update, and no way for a shipped feature to be silently unreachable.
/// </summary>
public interface IEndpoint
{
    void Map(IEndpointRouteBuilder routes);
}
