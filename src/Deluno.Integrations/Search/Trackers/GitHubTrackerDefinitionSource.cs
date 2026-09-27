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
    /// The GitHub Contents API for Prowlarr's definitions/v3 directory. Kept
    /// as a constant rather than a setting: the acceptance is "a configured
    /// remote source", not a source the user is expected to change, and a
    /// stable well-known default means every install fetches from the same
    /// place a person can go verify.
    /// </summary>
    public const string ContentsUrl = "https://api.github.com/repos/Prowlarr/Indexers/contents/definitions/v3";

    public async Task<IReadOnlyList<RemoteTrackerDefinition>> FetchAsync(CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        var entries = await http.GetFromJsonAsync<GitHubContentEntry[]>(ContentsUrl, cancellationToken) ?? [];

        var results = new List<RemoteTrackerDefinition>();
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "file", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.Name)
                || !entry.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.DownloadUrl))
            {
                continue;
            }

            var yaml = await http.GetStringAsync(entry.DownloadUrl, cancellationToken);
            results.Add(new RemoteTrackerDefinition(entry.Name[..^".yml".Length], yaml));
        }

        return results;
    }

    private sealed record GitHubContentEntry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("download_url")] string? DownloadUrl);
}
