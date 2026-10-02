using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Deluno.Contracts;

namespace Deluno.Connections.Versions;

/// <summary>Where one project publishes its releases (#1117).</summary>
public abstract record ReleaseFeed(string Project)
{
    /// <summary>Builds the request that asks the feed for its newest releases.</summary>
    public abstract Uri RequestUri { get; }

    /// <summary>Picks the newest stable release out of the feed's answer, or null.</summary>
    public abstract LatestRelease? Pick(string body);
}

/// <summary>A GitHub project that publishes releases; <c>releases/latest</c> already leaves out drafts and prereleases.</summary>
public sealed record GitHubReleasesFeed(string Project, string Repository) : ReleaseFeed(Project)
{
    public override Uri RequestUri => new($"https://api.github.com/repos/{Repository}/releases/latest");

    public override LatestRelease? Pick(string body) => ReleaseFeedParsers.FromGitHubRelease(body);
}

/// <summary>A GitHub project that only tags its releases (qBittorrent "release-5.2.4", Deluge "deluge-2.2.0").</summary>
public sealed record GitHubTagsFeed(string Project, string Repository, string TagPrefix) : ReleaseFeed(Project)
{
    public override Uri RequestUri => new($"https://api.github.com/repos/{Repository}/tags?per_page=100");

    public override LatestRelease? Pick(string body) => ReleaseFeedParsers.FromGitHubTags(body, Repository, TagPrefix);
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
        var ok = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, feed.RequestUri);
            request.Headers.UserAgent.ParseAdd("Deluno");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                release = feed.Pick(await response.Content.ReadAsStringAsync(timeout.Token));
                ok = release is not null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        lock (entry.Gate)
        {
            if (ok)
            {
                entry.Release = release;
            }

            entry.NextDueUtc = timeProvider.GetUtcNow() + (ok ? RefreshEvery : RetryAfterFailure);
        }
    }
}
