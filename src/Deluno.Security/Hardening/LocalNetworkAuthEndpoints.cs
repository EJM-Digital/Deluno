using System.Net;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security.Hardening;

/// <summary>
/// The handler bodies behind <c>/api/security/local-network-auth</c> (#993),
/// pulled out of the minimal-API lambdas the same way Allowed Hosts' are.
/// </summary>
public static class LocalNetworkAuthEndpoints
{
    public static async Task<IResult> GetOverviewAsync(
        HttpContext httpContext,
        ILocalNetworkAuthRepository repository,
        IAllowedHostsRepository allowedHostsRepository,
        ITrustedProxyClientAddressResolver resolver,
        CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        var allowedHosts = await allowedHostsRepository.GetAsync(cancellationToken);
        var resolvedAddress = await resolver.ResolveAsync(httpContext, cancellationToken);
        var currentAddress = resolvedAddress ?? httpContext.Connection.RemoteIpAddress;
        var currentAddressText = currentAddress?.ToString() ?? "unknown";

        var isLocal = currentAddress is not null && LocalNetworkMatcher.IsLocal(currentAddress, settings.AdditionalLocalNetworks);
        var isTrusted = isLocal && (settings.AllowList.Count == 0 || LocalNetworkMatcher.MatchesAny(currentAddress!, settings.AllowList));

        return Results.Ok(new LocalNetworkAuthOverview(
            settings.Enabled,
            settings.AdditionalLocalNetworks,
            settings.AllowList,
            settings.TrustedProxies,
            currentAddressText,
            isLocal,
            isTrusted,
            allowedHosts.Enabled));
    }

    public static async Task<IResult> SaveAsync(
        UpdateLocalNetworkAuthRequest request,
        HttpContext httpContext,
        ILocalNetworkAuthRepository repository,
        ILocalNetworkAuthSettingsCache settingsCache,
        ILocalNetworkAuthChangeNotifier changeNotifier,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        var additionalNetworks = ValidateEntries(request.AdditionalLocalNetworks, errors, "additionalLocalNetworks");
        var allowList = ValidateEntries(request.AllowList, errors, "allowList");
        var trustedProxies = ValidateEntries(request.TrustedProxies, errors, "trustedProxies");

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var previous = await repository.GetAsync(cancellationToken);
        var settings = new LocalNetworkAuthSettings(request.Enabled, additionalNetworks, allowList, trustedProxies);
        await repository.SaveAsync(settings, cancellationToken);
        settingsCache.Invalidate();

        if (settings.Enabled != previous.Enabled)
        {
            await changeNotifier.NotifyChangedAsync(settings.Enabled, cancellationToken);
        }

        return Results.Ok(settings);
    }

    private static string[] ValidateEntries(IReadOnlyList<string>? entries, Dictionary<string, string[]> errors, string field)
    {
        var trimmed = (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var invalid = trimmed.Where(entry => !LocalNetworkMatcher.IsValidEntry(entry)).ToArray();
        if (invalid.Length > 0)
        {
            errors[field] =
            [
                $"'{string.Join("', '", invalid)}' is not a valid address or subnet. Use an IP address (192.168.1.20) or a CIDR range (192.168.1.0/28)."
            ];
        }

        return trimmed;
    }
}
