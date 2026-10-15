using Deluno.Contracts;
using Deluno.Integrations.Search.Trackers;
using Deluno.Platform.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// The Cloudflare helper set in Preferences, General (#1027) answers at its address.
/// Without this, a helper that is not running only showed up as an indexer
/// test failing once a tracker put a Cloudflare check in front of a search.
/// Silent when no helper address is set. It uses the same probe as the
/// Settings test button.
/// </summary>
public sealed class CloudflareHelperHealthCheck(
    IPlatformSettingsRepository settings,
    IHttpClientFactory httpClientFactory) : IHealthCheck
{
    private const string Id = "system:cloudflare-helper";
    private const string Name = "Cloudflare helper";

    public string Category => HealthCheckCategories.Indexer;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var configured = (await settings.GetAsync(cancellationToken)).CloudflareHelperUrl;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        if (!WebAddress.TryParse(configured, out var address))
        {
            return [HealthCheckOutcome.Problem(
                Id, Category, Name,
                $"The Cloudflare helper address \"{configured.Trim()}\" is not a web address.",
                cause: "Trackers behind a Cloudflare check cannot be searched without a working helper.",
                fix: "Fix the helper's address in Preferences, General, such as http://127.0.0.1:8191.")];
        }

        var http = httpClientFactory.CreateClient(FlareSolverrCloudflareHelper.HttpClientName);
        var (healthy, message) = await CloudflareHelperProbe.CheckAsync(http, address, cancellationToken);
        return [healthy
            ? HealthCheckOutcome.Ok(Id, Category, Name, message)
            : HealthCheckOutcome.Problem(
                Id, Category, Name,
                $"The Cloudflare helper at {address.Authority} did not answer. {message}",
                cause: "Indexers behind a Cloudflare check cannot be searched until it answers.",
                fix: "Start the helper (FlareSolverr or Byparr), or fix its address in Preferences, General.")];
    }
}
