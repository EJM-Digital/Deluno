using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The remote source #756 fetches updated tracker (Cardigann-style)
/// definitions from: Prowlarr's own public definitions repository, the same
/// upstream project Deluno's built-in definitions were themselves drawn from.
/// Reads the repository's file listing, then downloads each YAML file's raw
/// content directly -- two GitHub API calls' worth of requests become one
/// listing call plus one small file each, never a full repository clone.
/// </summary>
public sealed class GitHubTrackerDefinitionSource(IHttpClientFactory httpClientFactory) : IRemoteTrackerDefinitionSource
{
    public const string HttpClientName = "tracker-definitions-source";

    /// <summary>
    /// The GitHub Contents API for Prowlarr's definitions/v11 directory, the
    /// Cardigann schema version the bundled snapshot (#986) is also taken
    /// from and the one Prowlarr's own VERSIONS file names as current. Kept
    /// as a constant rather than a setting: the acceptance is "a configured
    /// remote source", not a source the user is expected to change, and a
    /// stable well-known default means every install fetches from the same
    /// place a person can go verify.
    /// </summary>
    public const string ContentsUrl = "https://api.github.com/repos/Prowlarr/Indexers/contents/definitions/v11";

    public async Task<IReadOnlyList<RemoteTrackerDefinition>> FetchAsync(CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        var entries = await http.GetFromJsonAsync<GitHubContentEntry[]>(ContentsUrl, cancellationToken) ?? [];

        var wanted = entries.Where(entry => string.Equals(entry.Type, "file", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entry.Name)
            && entry.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entry.DownloadUrl)).ToArray();

        // Hundreds of small files: a few at a time, not one after another.
        using var gate = new SemaphoreSlim(6);
        var downloads = wanted.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var yaml = await http.GetStringAsync(entry.DownloadUrl!, cancellationToken);
                return new RemoteTrackerDefinition(entry.Name![..^".yml".Length], yaml);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(downloads);
    }

    private sealed record GitHubContentEntry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("download_url")] string? DownloadUrl);
}
