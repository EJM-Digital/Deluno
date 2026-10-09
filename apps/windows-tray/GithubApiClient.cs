using Deluno.Infrastructure.Http;
using Velopack.Sources;

namespace Deluno.Tray;

/// <summary>
/// The tray's calls to <c>api.github.com</c> (#1181). They go through the same <see cref="GitHubApiConditionalHandler"/>
/// the server uses, so the tray keeps each address's ETag and body, answers a 304 from them, and recognises GitHub's
/// hourly limit once, remembers it until the reset, and says it in the server's own sentence. The free routes
/// (<c>releases.atom</c> and <c>releases/download</c>) stay first; this is only what is used when they cannot answer.
/// </summary>
internal static class GithubApiClient
{
    /// <summary>
    /// A client whose api.github.com calls respect the limit. An injected handler (a test's) gets its own state unless
    /// one is given, so a limit it simulates never leaks into another caller; the real handler shares the process's.
    /// </summary>
    public static HttpClient Create(HttpMessageHandler? inner, string userAgent, TimeSpan timeout, GitHubApiState? state = null, TimeProvider? time = null)
    {
        var conditional = new GitHubApiConditionalHandler(
            state ?? (inner is null ? GitHubApiState.Shared : new GitHubApiState()),
            time ?? TimeProvider.System)
        {
            InnerHandler = inner ?? LoopbackFirstConnection.CreateHandler()
        };
        var http = new HttpClient(conditional, disposeHandler: inner is null) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    /// <summary>The limit, when this failure (or what caused it) is GitHub's hourly limit as the shared handler recognised it.</summary>
    public static GitHubRateLimitException? LimitOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is GitHubRateLimitException limit)
            {
                return limit;
            }
        }

        return null;
    }
}

/// <summary>
/// Velopack's GitHub source asks the API through this: the listing of releases is fetched with the shared conditional
/// handler, and the downloads of update files go to the ordinary downloader untouched.
/// </summary>
internal sealed class GithubApiFileDownloader(IFileDownloader files, HttpMessageHandler? apiHandler = null, GitHubApiState? state = null, TimeProvider? time = null) : IFileDownloader
{
    public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        => files.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        => files.DownloadBytes(url, headers, timeout);

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        using var http = GithubApiClient.Create(apiHandler, "Deluno-Updater", TimeSpan.FromSeconds(timeout), state, time);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
