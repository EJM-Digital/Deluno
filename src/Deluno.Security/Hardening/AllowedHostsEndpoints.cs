using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security.Hardening;

/// <summary>
/// The handler bodies behind <c>/api/security/allowed-hosts</c> (#939),
/// pulled out of the minimal-API lambdas so the lock-out rule can be unit
/// tested without standing up routing.
/// </summary>
public static class AllowedHostsEndpoints
{
    public static async Task<IResult> GetOverviewAsync(
        HttpContext httpContext,
        IAllowedHostsRepository repository,
        IAllowedHostsCandidateProvider candidateProvider,
        CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        var candidates = candidateProvider.DetectCandidates(httpContext);
        return Results.Ok(new AllowedHostsOverview(
            settings.Enabled,
            settings.AllowedHosts,
            candidates,
            AllowedHostMatcher.StripPort(httpContext.Request.Host.Host)));
    }

    public static async Task<IResult> SaveAsync(
        UpdateAllowedHostsRequest request,
        HttpContext httpContext,
        IAllowedHostsRepository repository,
        IAllowedHostsSettingsCache settingsCache,
        CancellationToken cancellationToken)
    {
        var currentHost = httpContext.Request.Host.Host;
        var hosts = (request.AllowedHosts ?? [])
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim())
            .ToArray();

        if (request.Enabled &&
            !AllowedHostMatcher.IsAlwaysAllowedLoopbackHost(currentHost) &&
            !AllowedHostMatcher.Matches(currentHost, hosts))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["allowedHosts"] =
                [
                    $"Deluno won't save this: the address you're using right now ({AllowedHostMatcher.StripPort(currentHost)}) is not on the list, and saving would lock you out. Add it first, or use localhost."
                ]
            });
        }

        var settings = new AllowedHostsSettings(request.Enabled, hosts);
        await repository.SaveAsync(settings, cancellationToken);
        settingsCache.Invalidate();
        return Results.Ok(settings);
    }
}
