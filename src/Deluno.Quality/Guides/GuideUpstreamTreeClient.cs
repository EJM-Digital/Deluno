using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Quality.Guides;

/// <summary>
/// Reads only the public TRaSH Git tree. The caller compares immutable blob
/// identifiers against the pinned package; it never downloads a mutable guide
/// package, applies it, or makes it part of a release decision.
/// </summary>
public sealed class GuideUpstreamTreeClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "trash-guide-update-check";
    private const string RepositoryPath = "repos/TRaSH-Guides/Guides";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GuideUpstreamTreeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var reference = await GetAsync<GitReference>(client, $"{RepositoryPath}/git/ref/heads/master", cancellationToken);
        var commitSha = reference.Object.Sha;
        var commit = await GetAsync<GitCommit>(client, $"{RepositoryPath}/git/commits/{commitSha}", cancellationToken);
        var tree = await GetAsync<GitTree>(client, $"{RepositoryPath}/git/trees/{commit.Tree.Sha}?recursive=1", cancellationToken);
        if (tree.Truncated)
        {
            throw new InvalidOperationException("The upstream Git tree was truncated, so Deluno cannot safely claim this guide check is complete.");
        }

        var blobs = (tree.Tree ?? [])
            .Where(item => string.Equals(item.Type, "blob", StringComparison.Ordinal))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) && !string.IsNullOrWhiteSpace(item.Sha))
            .ToDictionary(item => item.Path, item => item.Sha, StringComparer.Ordinal);
        return new GuideUpstreamTreeSnapshot(commitSha, blobs);
    }

    private static async Task<T> GetAsync<T>(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"TRaSH Guides update check returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("TRaSH Guides returned an empty Git response.");
    }
    private sealed record GitReference(GitObject Object);
    private sealed record GitObject(string Sha);
    private sealed record GitCommit(GitCommitTree Tree);
    private sealed record GitCommitTree(string Sha);
    private sealed record GitTree(bool Truncated, IReadOnlyList<GitTreeEntry>? Tree);
    private sealed record GitTreeEntry(string Path, string Type, string Sha);
}

public sealed record GuideUpstreamTreeSnapshot(
    string Revision,
    IReadOnlyDictionary<string, string> BlobShaByPath);
