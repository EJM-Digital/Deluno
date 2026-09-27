using Deluno.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// #816: keeps a configured custom remote URL's patterns fresh, the way
/// Cleanuparr auto-reloads its own remote lists — Deluno picks one interval
/// (<see cref="ContentBlocklistDefaults.RemoteRefreshInterval"/>, 4 hours)
/// rather than matching Cleanuparr's two-tier official/other timing. Does
/// nothing while no remote URL is configured, or while the content blocklist
/// itself is off — there is nothing to check files against either way.
/// </summary>
public sealed class ContentBlocklistRemoteRefreshService(
    ILogger<ContentBlocklistRemoteRefreshService> logger,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Content-blocklist remote refresh failed; it will try again on its next pass.");
            }

            try
            {
                await Task.Delay(ContentBlocklistDefaults.RemoteRefreshInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one refresh pass immediately; public so a test can drive it without waiting on the interval.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var healthRepository = scope.ServiceProvider.GetRequiredService<IDownloadHealthRepository>();
        var settings = await healthRepository.GetContentBlocklistSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.RemoteUrl))
        {
            return;
        }

        if (!Uri.TryCreate(settings.RemoteUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("The content blocklist's remote URL is not a usable http(s) address; nothing was fetched.");
            return;
        }

        try
        {
            var http = httpClientFactory.CreateClient("download-clients");
            http.Timeout = TimeSpan.FromSeconds(20);
            var body = await http.GetStringAsync(uri, cancellationToken);
            var patterns = ParsePatterns(body);
            await healthRepository.RecordContentBlocklistRemoteRefreshAsync(patterns, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not fetch the content blocklist's remote URL; the patterns it already holds are kept.");
        }
    }

    /// <summary>
    /// One pattern per non-empty, non-comment line - the same plain-text
    /// shape Cleanuparr's own official lists (cleanuparr.pages.dev) and a
    /// hand-maintained custom list both use. A line starting with '#' is a
    /// comment, not a pattern.
    /// </summary>
    private static IReadOnlyList<string> ParsePatterns(string body)
        => body
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
