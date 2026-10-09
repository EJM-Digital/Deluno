using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Http;

namespace Deluno.Connections.Versions;

/// <summary>Where one project publishes its releases (#1117).</summary>
public abstract record ReleaseFeed(string Project)
{
    /// <summary>Builds the request that asks the feed for its newest releases.</summary>
    public abstract Uri RequestUri { get; }

    /// <summary>Picks the newest stable release out of the feed's answer, or null.</summary>
    public abstract LatestRelease? Pick(string body);

    /// <summary>
    /// A way to read the newest release that does not count against GitHub's 60 API calls an hour for a network
    /// (#1181): the project's releases.atom page. Null for a feed that has none. It is tried first; the API
    /// (<see cref="RequestUri"/>) only when this cannot be read.
    /// </summary>
    public virtual Uri? FreeRequestUri => null;

    /// <summary>Picks the newest release out of the free route's answer, or null.</summary>
    public virtual LatestRelease? PickFree(string body) => null;
}

/// <summary>
/// A GitHub project that publishes releases. By default <c>releases/latest</c>, which already leaves out drafts
/// and prereleases. With <paramref name="IncludePreReleases"/> (Weir, whose releases are release candidates, #1123)
/// the newest release by version order, pre-releases included, drafts never.
/// </summary>
public sealed record GitHubReleasesFeed(string Project, string Repository, bool IncludePreReleases = false) : ReleaseFeed(Project)
{
    public override Uri RequestUri => IncludePreReleases
        ? new($"https://api.github.com/repos/{Repository}/releases?per_page=100")
        : new($"https://api.github.com/repos/{Repository}/releases/latest");

    public override LatestRelease? Pick(string body)
        => IncludePreReleases ? ReleaseFeedParsers.FromGitHubReleaseList(body) : ReleaseFeedParsers.FromGitHubRelease(body);

    public override Uri? FreeRequestUri => ReleaseAtomFeed.ReleasesUri(Repository);

    public override LatestRelease? PickFree(string body)
        => ReleaseFeedParsers.FromAtom(body, Repository, includePreReleases: IncludePreReleases, tagPrefix: "");
}

/// <summary>A GitHub project that only tags its releases (qBittorrent "release-5.2.4", Deluge "deluge-2.2.0").</summary>
public sealed record GitHubTagsFeed(string Project, string Repository, string TagPrefix) : ReleaseFeed(Project)
{
    public override Uri RequestUri => new($"https://api.github.com/repos/{Repository}/tags?per_page=100");

    public override LatestRelease? Pick(string body) => ReleaseFeedParsers.FromGitHubTags(body, Repository, TagPrefix);

    public override Uri? FreeRequestUri => ReleaseAtomFeed.TagsUri(Repository);

    public override LatestRelease? PickFree(string body)
        => ReleaseFeedParsers.FromAtom(body, Repository, includePreReleases: false, tagPrefix: TagPrefix);
}

/// <summary>Plex's own downloads feed, the public Plex Media Server build.</summary>
public sealed record PlexDownloadsFeed(string Project) : ReleaseFeed(Project)
{
    public override Uri RequestUri => new("https://plex.tv/api/downloads/5.json");

    public override LatestRelease? Pick(string body) => ReleaseFeedParsers.FromPlexDownloads(body);
}

/// <summary>Reads each feed's answer. Anything unexpected is null: no release known, so no "update available".</summary>
public static class ReleaseFeedParsers
{
    public static LatestRelease? FromGitHubRelease(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                || (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var version = StableVersion(tag.GetString());
            var url = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String ? SafeUrl(html.GetString()) : null;
            return version is null ? null : new LatestRelease(version, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The newest release in a <c>/releases</c> list by version order, pre-releases included, drafts never (#1123).</summary>
    public static LatestRelease? FromGitHubReleaseList(string body)
    {
        if (ReleaseVersions.NewestGitHubRelease(body) is not { } root)
        {
            return null;
        }

        var text = root.GetProperty("tag_name").GetString()!.Trim();
        if (text.Length > 1 && (text[0] is 'v' or 'V') && char.IsDigit(text[1]))
        {
            text = text[1..];
        }

        var url = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String ? SafeUrl(html.GetString()) : null;
        return new LatestRelease(text, url);
    }

    /// <summary>
    /// The newest release out of a github.com releases.atom or tags.atom page (#1181), by version order.
    /// Release candidates only when asked for; a draft is never in the page. The version text has no tag prefix and no leading "v".
    /// </summary>
    public static LatestRelease? FromAtom(string atom, string repository, bool includePreReleases, string tagPrefix)
    {
        if (ReleaseAtomFeed.NewestTag(atom, includePreReleases, tagPrefix) is not { } tag)
        {
            return null;
        }

        var text = tag[tagPrefix.Length..].Trim();
        if (text.Length > 1 && (text[0] is 'v' or 'V') && char.IsDigit(text[1]))
        {
            text = text[1..];
        }

        return new LatestRelease(text, ReleaseAtomFeed.TagPageUrl(repository, tag));
    }

    public static LatestRelease? FromGitHubTags(string body, string repository, string tagPrefix)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestVersion = null;
            string? bestTag = null;
            ParsedVersion? best = null;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                if (name is null || !name.StartsWith(tagPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var version = StableVersion(name[tagPrefix.Length..]);
                var parsed = ReleaseVersions.Parse(version);
                if (version is null || parsed is null || (best is not null && ReleaseVersions.Compare(parsed, best) <= 0))
                {
                    continue;
                }

                best = parsed;
                bestVersion = version;
                bestTag = name;
            }

            return bestVersion is null ? null : new LatestRelease(bestVersion, $"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(bestTag!)}");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Plex's downloads feed: <c>computer.Windows.version</c> (any platform will do, they match), "1.43.4.10903-e5521bd8c".</summary>
    public static LatestRelease? FromPlexDownloads(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("computer", out var computer)
                || computer.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var platform in new[] { "Windows", "Linux", "MacOS" })
            {
                if (computer.TryGetProperty(platform, out var entry)
                    && entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && StableVersion(version.GetString()) is { } stable)
                {
                    return new LatestRelease(stable, "https://www.plex.tv/media-server-downloads/");
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The version text of a tag when it is a stable release ("v5.0.2" becomes "5.0.2"); null for a beta, release candidate or anything unreadable.</summary>
    private static string? StableVersion(string? tag)
    {
        var text = tag?.Trim();
        if (text is { Length: > 1 } && (text[0] is 'v' or 'V') && char.IsDigit(text[1]))
        {
            text = text[1..];
        }

        return ReleaseVersions.Parse(text) is { IsPreRelease: false } ? text : null;
    }

    private static string? SafeUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
}

/// <summary>
/// The newest stable release of each project, from its public feed (#1117). Answers from memory and never waits:
/// a project's feed is read in the background at most once a day, so a connection test or a page load is never
/// slowed or failed by it, and GitHub's 60 requests an hour for an anonymous caller is never in reach. A feed that
/// cannot be read is tried again an hour later, and meanwhile the last good answer (or none) stands.
/// </summary>
public sealed class LatestReleaseService : ILatestReleaseService
{
    public const string HttpClientName = "release-feeds";

    /// <summary>How long a feed's answer is kept before the feed is asked again.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);

    /// <summary>How long after a failed read the feed is tried again.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public readonly object Gate = new();
        public LatestRelease? Release;
        public DateTimeOffset NextDueUtc = DateTimeOffset.MinValue;
        public Task? InFlight;
        /// <summary>When GitHub's limit stopped the last read and lifts; null when it did not. Cleared by the next read.</summary>
        public DateTimeOffset? LimitResetsUtc;
    }

    private readonly IHttpClientFactory httpClientFactory;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, ReleaseFeed> feeds;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    public LatestReleaseService(IHttpClientFactory httpClientFactory, IEnumerable<ReleaseFeed> feeds, TimeProvider timeProvider)
    {
        this.httpClientFactory = httpClientFactory;
        this.timeProvider = timeProvider;
        this.feeds = feeds.ToDictionary(feed => feed.Project, StringComparer.OrdinalIgnoreCase);
    }

    public DateTimeOffset? LimitResetsUtc(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || !entries.TryGetValue(project, out var entry))
        {
            return null;
        }

        lock (entry.Gate)
        {
            return entry.LimitResetsUtc is { } until && until > timeProvider.GetUtcNow() ? until : null;
        }
    }

    public LatestRelease? Peek(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || !feeds.ContainsKey(project))
        {
            return null;
        }

        var entry = entries.GetOrAdd(project, _ => new Entry());
        bool due;
        lock (entry.Gate)
        {
            due = (entry.InFlight is null or { IsCompleted: true }) && entry.NextDueUtc <= timeProvider.GetUtcNow();
        }

        if (due)
        {
            _ = Task.Run(() => RefreshIfDueAsync(project, CancellationToken.None));
        }

        lock (entry.Gate)
        {
            return entry.Release;
        }
    }

    /// <summary>
    /// Asks the project's feed if the last answer is more than a day old (or an hour after a failure), and waits
    /// for it. Never throws. <see cref="Peek"/> calls this in the background; it is public so a test can wait.
    /// </summary>
    public async Task RefreshIfDueAsync(string project, CancellationToken cancellationToken)
    {
        if (!feeds.TryGetValue(project, out var feed))
        {
            return;
        }

        var entry = entries.GetOrAdd(project, _ => new Entry());
        Task work;
        lock (entry.Gate)
        {
            if (entry.InFlight is { IsCompleted: false })
            {
                work = entry.InFlight;
            }
            else if (entry.NextDueUtc > timeProvider.GetUtcNow())
            {
                return;
            }
            else
            {
                work = entry.InFlight = FetchAsync(entry, feed, cancellationToken);
            }
        }

        try
        {
            await work;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A feed that cannot be read is "no newer version known", nothing more.
        }
    }

    private async Task FetchAsync(Entry entry, ReleaseFeed feed, CancellationToken cancellationToken)
    {
        LatestRelease? release = null;
        DateTimeOffset? limit = null;
        try
        {
            // The free route first (github.com's own release page, which GitHub's API allowance does not cover),
            // the API only when that cannot be read (#1181).
            if (feed.FreeRequestUri is { } free)
            {
                try
                {
                    release = await TryReadAsync(free, feed.PickFree, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // Not readable that way: the API below is the limited route.
                }
            }

            release ??= await TryReadAsync(feed.RequestUri, feed.Pick, cancellationToken);
        }
        catch (GitHubRateLimitException exception)
        {
            limit = exception.ResetsAt;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        var ok = release is not null;
        lock (entry.Gate)
        {
            if (ok)
            {
                entry.Release = release;
            }

            entry.LimitResetsUtc = limit;
            // Not again before GitHub's limit lifts; otherwise a day after a good read, an hour after a failed one.
            entry.NextDueUtc = limit is { } reset
                ? reset
                : timeProvider.GetUtcNow() + (ok ? RefreshEvery : RetryAfterFailure);
        }
    }

    /// <summary>One read of one address. Null when it answered something that is not a release; a limit or a network fault throws.</summary>
    private async Task<LatestRelease?> TryReadAsync(Uri uri, Func<string, LatestRelease?> pick, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Deluno");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(uri.Host == "github.com" ? "application/atom+xml" : "application/json"));
        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
        return response.IsSuccessStatusCode ? pick(await response.Content.ReadAsStringAsync(timeout.Token)) : null;
    }
}
