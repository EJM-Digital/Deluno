using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Deluno.Quality.Guides;

/// <summary>
/// Reads the public TRaSH Git tree and, only for an explicit owner-requested
/// sync preview, its archive at an immutable commit. Nothing fetched here is
/// a runtime release-decision input: package validation and owner apply remain
/// separate steps.
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

    /// <summary>
    /// Downloads one archive pinned by commit SHA and extracts only the fixed
    /// Deluno-supported JSON roots. GitHub's tree is read first, so every
    /// retained source still has its immutable blob identity.
    /// </summary>
    public async Task<GuideSourceInventory> GetSourceInventoryAsync(
        GuideUpstreamTreeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Revision))
            throw new InvalidDataException("TRaSH Guides did not provide an immutable revision for this sync.");

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://codeload.github.com/TRaSH-Guides/Guides/zip/{Uri.EscapeDataString(snapshot.Revision)}");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"TRaSH Guides source archive returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        const long maximumArchiveBytes = 50L * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > maximumArchiveBytes)
            throw new InvalidDataException("The TRaSH Guides source archive is unexpectedly large, so Deluno did not stage a sync.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        var sourceTextByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var retainedBytes = 0L;
        foreach (var entry in archive.Entries)
        {
            var path = RemoveArchiveRoot(entry.FullName);
            if (!GuideUpstreamSourceInventoryBuilder.IsTrackedSourcePath(path)) continue;
            if (entry.Length > 1_024 * 1_024)
                throw new InvalidDataException($"The TRaSH Guides source '{path}' is unexpectedly large, so Deluno did not stage a sync.");
            retainedBytes += entry.Length;
            if (retainedBytes > 20L * 1024 * 1024)
                throw new InvalidDataException("The retained TRaSH Guides source data is unexpectedly large, so Deluno did not stage a sync.");
            await using var entryStream = entry.Open();
            using var sourceBytes = new MemoryStream((int)entry.Length);
            await entryStream.CopyToAsync(sourceBytes, cancellationToken);
            var bytes = sourceBytes.ToArray();
            if (snapshot.BlobShaByPath.TryGetValue(path, out var blobSha)
                && blobSha.Length == 40
                && !string.Equals(ComputeGitBlobSha(bytes), blobSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The TRaSH Guides archive does not match pinned blob '{path}', so Deluno did not stage a sync.");
            }
            var sourceText = Encoding.UTF8.GetString(bytes);
            sourceTextByPath[path] = sourceText.Length > 0 && sourceText[0] == '\uFEFF'
                ? sourceText[1..]
                : sourceText;
        }

        return GuideUpstreamSourceInventoryBuilder.Build(snapshot, sourceTextByPath);
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

    private static string RemoveArchiveRoot(string fullName)
    {
        var separator = fullName.IndexOf('/');
        return separator < 0 || separator == fullName.Length - 1
            ? string.Empty
            : fullName[(separator + 1)..];
    }

    private static string ComputeGitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        var input = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, input, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, input, header.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant();
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
