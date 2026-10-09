using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The remote source #756 fetches updated tracker (Cardigann-style)
/// definitions from: Prowlarr's own public definitions repository, the same
/// upstream project Deluno's built-in definitions were themselves drawn from.
/// Downloads the repository as one archive from codeload.github.com, which
/// spends none of GitHub's API allowance; only if that cannot be read does it
/// fall back to the API's file listing plus one small file each.
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

    /// <summary>
    /// The whole repository as one archive. codeload.github.com is not api.github.com, so it spends none of the 60
    /// calls an hour GitHub gives one network (#1181), and it is one request instead of a listing plus a file each.
    /// </summary>
    public const string ArchiveUrl = "https://codeload.github.com/Prowlarr/Indexers/zip/refs/heads/master";

    /// <summary>The folder inside the archive (below its single top folder) that holds the definitions.</summary>
    private const string DefinitionsFolder = "definitions/v11/";

    /// <summary>The biggest archive Deluno will take.</summary>
    private const long MaxArchiveBytes = 100L * 1024 * 1024;

    public async Task<IReadOnlyList<RemoteTrackerDefinition>> FetchAsync(CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            var fromArchive = await FetchArchiveAsync(http, cancellationToken);
            if (fromArchive.Count > 0)
            {
                return fromArchive;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The archive could not be read: use the API listing below, which is the slower, limited way.
        }

        return await FetchViaApiAsync(http, cancellationToken);
    }

    private static async Task<IReadOnlyList<RemoteTrackerDefinition>> FetchArchiveAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxArchiveBytes)
        {
            throw new InvalidDataException("The definitions archive is unexpectedly large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Read);
        var found = new List<RemoteTrackerDefinition>();
        foreach (var entry in archive.Entries)
        {
            // "Indexers-master/definitions/v11/name.yml": the first folder is the archive's own.
            var slash = entry.FullName.IndexOf('/');
            if (slash < 0)
            {
                continue;
            }

            var path = entry.FullName[(slash + 1)..];
            if (!path.StartsWith(DefinitionsFolder, StringComparison.Ordinal)
                || !path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || path.IndexOf('/', DefinitionsFolder.Length) >= 0
                || entry.Length > 1024 * 1024)
            {
                continue;
            }

            await using var file = entry.Open();
            using var reader = new StreamReader(file);
            var yaml = await reader.ReadToEndAsync(cancellationToken);
            found.Add(new RemoteTrackerDefinition(path[DefinitionsFolder.Length..^".yml".Length], yaml));
        }

        return found;
    }

    private static async Task<IReadOnlyList<RemoteTrackerDefinition>> FetchViaApiAsync(HttpClient http, CancellationToken cancellationToken)
    {
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
