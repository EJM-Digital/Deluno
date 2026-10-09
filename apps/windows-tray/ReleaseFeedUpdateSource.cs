using Deluno.Contracts;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Deluno.Tray;

/// <summary>
/// Where Deluno looks for its own updates (#1181). Velopack's <see cref="GithubSource"/> asks api.github.com,
/// and an unauthenticated network gets only 60 of those an hour, shared with every other app on it. This finds the
/// newest tag on github.com's own release feed (<c>releases.atom</c>, which spends none of them), release
/// candidates included when the channel wants them, and reads that tag's update files from its
/// <c>releases/download/&lt;tag&gt;/</c> folder with <see cref="SimpleWebSource"/>. The API-based source is kept only
/// as the fallback for a feed that cannot be read.
/// </summary>
public sealed class ReleaseFeedUpdateSource : IUpdateSource
{
    private readonly string repositoryUrl;
    private readonly bool includePreReleases;
    private readonly IFileDownloader? downloader;
    private readonly HttpMessageHandler? feedHandler;
    private IUpdateSource? chosen;

    public ReleaseFeedUpdateSource(string repositoryUrl, bool includePreReleases, IFileDownloader? downloader = null, HttpMessageHandler? feedHandler = null)
    {
        this.repositoryUrl = repositoryUrl;
        this.includePreReleases = includePreReleases;
        this.downloader = downloader;
        this.feedHandler = feedHandler;
    }

    /// <summary>"owner/name" for a github.com repository address; null for any other address.</summary>
    public static string? RepositoryOf(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase)}" : null;
    }

    /// <summary>The newest tag on the repository's release feed, or null when the feed cannot be read.</summary>
    public static async Task<string?> FindNewestTagAsync(string repository, bool includePreReleases, HttpMessageHandler? handler, CancellationToken cancellationToken)
        => (await LookUpNewestTagAsync(repository, includePreReleases, handler, cancellationToken)).Tag;

    /// <summary>
    /// The newest tag, and whether the feed answered 404. github.com answers 404 for a repository that is private or
    /// does not exist, and the API answers the same for it while still spending one of the hourly calls, so a 404 is
    /// final (#1181).
    /// </summary>
    public static async Task<(string? Tag, bool NotFound)> LookUpNewestTagAsync(string repository, bool includePreReleases, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        try
        {
            using var http = handler is null
                ? new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler())
                : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Deluno-Updater");
            using var response = await http.GetAsync(ReleaseAtomFeed.ReleasesUri(repository), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (null, true);
            }

            return response.IsSuccessStatusCode
                ? (ReleaseAtomFeed.NewestTag(await response.Content.ReadAsStringAsync(cancellationToken), includePreReleases), false)
                : (null, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, false);
        }
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var repository = RepositoryOf(repositoryUrl);
        string? tag = null;
        if (repository is not null)
        {
            var lookup = await LookUpNewestTagAsync(repository, includePreReleases, feedHandler, CancellationToken.None);
            if (lookup.NotFound)
            {
                // Private or missing repository: the API would answer 404 too, and the call still counts.
                throw new HttpRequestException($"The release feed for {repository} answered 404 (Not Found).", null, System.Net.HttpStatusCode.NotFound);
            }

            tag = lookup.Tag;
        }

        if (repository is not null && tag is not null)
        {
            var folder = new SimpleWebSource(ReleaseAtomFeed.DownloadFolderUrl(repository, tag), downloader);
            try
            {
                var feed = await folder.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
                chosen = folder;
                return feed;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Net.WebException)
            {
                // The newest tag's update files could not be read from its download folder: use the API below.
            }
        }

        var api = new GithubSource(repositoryUrl, accessToken: null, prerelease: includePreReleases, downloader);
        chosen = api;
        return await api.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        => (chosen ?? new GithubSource(repositoryUrl, accessToken: null, prerelease: includePreReleases, downloader))
            .DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
}
