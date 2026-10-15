using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// Cleanuparr's Internet Connectivity Check (#824): a person's own probe URLs,
/// checked before every cleanup pass. Deliberately separate from #901's own
/// outage detector - that one watches Deluno's own integrations; this one is
/// whatever the person decides proves "the internet is up" for them.
/// </summary>
public interface IDownloadHealthConnectivityCheck
{
    /// <summary>True once at least one comma-separated URL answers with a 2xx status. Empty input means nothing to check, so nothing is reachable.</summary>
    Task<bool> AnyReachableAsync(string commaSeparatedUrls, CancellationToken cancellationToken);
}

/// <summary>The safe default for a caller that never wired a real check: never wrongly skips a pass.</summary>
public sealed class AlwaysReachableConnectivityCheck : IDownloadHealthConnectivityCheck
{
    public static readonly AlwaysReachableConnectivityCheck Instance = new();
    public Task<bool> AnyReachableAsync(string commaSeparatedUrls, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class HttpDownloadHealthConnectivityCheck(IHttpClientFactory httpClientFactory) : IDownloadHealthConnectivityCheck
{
    public const string HttpClientName = "download-health-connectivity-check";

    public async Task<bool> AnyReachableAsync(string commaSeparatedUrls, CancellationToken cancellationToken)
    {
        var urls = (commaSeparatedUrls ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => WebAddress.IsValid(url))
            .ToArray();
        if (urls.Length == 0)
        {
            return false;
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        foreach (var url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Try the next URL; only "none of them answered" is reported.
            }
        }

        return false;
    }
}
