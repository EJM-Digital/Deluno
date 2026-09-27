using Deluno.Contracts.Connectivity;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Network;

/// <summary>
/// Backs the app-wide outage banner (#901): whether the internet is down,
/// judged by <see cref="IConnectivityMonitor"/>'s real connectivity probe
/// rather than any one download's own stall.
/// </summary>
public static class ConnectivityEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoConnectivityEndpoints(this IEndpointRouteBuilder api)
    {
        // [FromServices]: Deluno.Api does not reference Deluno.Platform, whose
        // module registers the real IConnectivityMonitor, so minimal API's
        // route-build-time DI check cannot see it and would otherwise infer
        // this parameter as a request body. Explicit [FromServices] resolves
        // it from the app's container at request time instead - the same
        // pattern every other cross-module interface here uses.
        api.MapGet("/system/connectivity", async ([FromServices] IConnectivityMonitor connectivityMonitor, CancellationToken cancellationToken)
                => Results.Ok(await connectivityMonitor.GetStatusAsync(cancellationToken)))
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        return api;
    }
}
