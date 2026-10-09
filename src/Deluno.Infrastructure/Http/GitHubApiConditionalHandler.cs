using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Deluno.Infrastructure.Http;

/// <summary>
/// GitHub lets one network make 60 unauthenticated API calls an hour, shared by every app on it (#1181).
/// This is the one place Deluno's calls to <c>api.github.com</c> respect that:
/// <list type="bullet">
/// <item>the last ETag and body of each address are kept, <c>If-None-Match</c> is sent, and a 304 (which does not
/// count against the limit) is answered from the stored body as a normal 200;</item>
/// <item>a 403 or 429 that says no calls remain is the limit, not an internet fault: it is remembered until the
/// reset time, every call before then fails at once without being sent, and the failure says so in plain words.</item>
/// </list>
/// Other hosts (release downloads, raw files) pass through untouched.
/// </summary>
public sealed class GitHubApiConditionalHandler : DelegatingHandler
{
    private readonly GitHubApiState state;
    private readonly TimeProvider timeProvider;

    public GitHubApiConditionalHandler() : this(GitHubApiState.Shared, TimeProvider.System)
    {
    }

    public GitHubApiConditionalHandler(GitHubApiState state, TimeProvider timeProvider)
    {
        this.state = state;
        this.timeProvider = timeProvider;
    }

    public static bool IsApiRequest(HttpRequestMessage request)
        => request.Method == HttpMethod.Get
            && request.RequestUri is { IsAbsoluteUri: true } uri
            && string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsApiRequest(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        if (state.BlockedUntil is { } until && until > now)
        {
            throw new GitHubRateLimitException(until);
        }

        var key = request.RequestUri!.AbsoluteUri;
        var stored = state.Bodies.TryGetValue(key, out var found) ? found : null;
        if (stored is not null && request.Headers.IfNoneMatch.Count == 0)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(stored.ETag));
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified && stored is not null)
        {
            response.Dispose();
            var reused = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(stored.Body) };
            if (stored.ContentType is not null && MediaTypeHeaderValue.TryParse(stored.ContentType, out var type))
            {
                reused.Content.Headers.ContentType = type;
            }

            return reused;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && ResetOf(response, now) is { } reset)
        {
            response.Dispose();
            state.BlockedUntil = reset;
            throw new GitHubRateLimitException(reset);
        }

        if (response.StatusCode == HttpStatusCode.OK && response.Headers.ETag is { } etag)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            state.Bodies[key] = new StoredResponse(etag.ToString(), bytes, response.Content.Headers.ContentType?.ToString());
        }

        return response;
    }

    /// <summary>When the limit lifts, if this answer says the limit is used up; otherwise null.</summary>
    private static DateTimeOffset? ResetOf(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault()?.Trim() == "0")
        {
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
                && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            return now.AddMinutes(15);
        }

        // GitHub's secondary limit has no remaining count, only how long to wait.
        if (response.Headers.RetryAfter is { } retry)
        {
            return retry.Date ?? now + (retry.Delta ?? TimeSpan.FromMinutes(1));
        }

        return null;
    }
}

public sealed record StoredResponse(string ETag, byte[] Body, string? ContentType);

/// <summary>What the handler remembers between calls: each address's last ETag and body, and when the limit lifts. One shared copy per process.</summary>
public sealed class GitHubApiState
{
    public static GitHubApiState Shared { get; } = new();

    public ConcurrentDictionary<string, StoredResponse> Bodies { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset? BlockedUntil { get; set; }
}

/// <summary>GitHub's hourly allowance for this network is used up. Its message is the plain sentence the person sees.</summary>
public sealed class GitHubRateLimitException(DateTimeOffset resetsAt)
    : HttpRequestException(MessageFor(resetsAt), null, HttpStatusCode.Forbidden)
{
    public DateTimeOffset ResetsAt { get; } = resetsAt;

    /// <summary>
    /// The plain sentence. The reset is written as a UTC instant ("2026-10-09 00:35 UTC") because the server's own
    /// clock zone means nothing to someone looking from elsewhere; the web app shows it in the viewer's local time
    /// (<c>localizeResetTimes</c>) and <see cref="LocalizeResetTimes"/> does the same on this computer.
    /// </summary>
    public static string MessageFor(DateTimeOffset resetsAt)
        => $"GitHub's limit for checking releases from this network is used up until {UtcStamp(resetsAt)}. Deluno will try again then.";

    /// <summary>"2026-10-09 00:35 UTC", the form the web app finds and shows in the viewer's time.</summary>
    public static string UtcStamp(DateTimeOffset instant)
        => instant.UtcDateTime.AddSeconds(59).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static readonly System.Text.RegularExpressions.Regex StampPattern = new(@"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}) UTC", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Replaces each UTC stamp in <paramref name="text"/> with the time in this computer's own zone ("5:35 pm"), for screens on the same computer as the server (the tray).</summary>
    public static string LocalizeResetTimes(string text, TimeZoneInfo? zone = null)
        => StampPattern.Replace(text, match =>
            DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
                ? TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local).ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant()
                : match.Value);
}
