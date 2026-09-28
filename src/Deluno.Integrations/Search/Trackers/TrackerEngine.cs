using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>What Deluno is looking for, as a tracker search asks it (#606).</summary>
/// <param name="Keywords">The search text, with the year or episode already in it.</param>
/// <param name="SearchType">search, movie or tvsearch.</param>
public sealed record TrackerQuery(
    string Keywords,
    string SearchType = "search",
    int? Season = null,
    int? Episode = null,
    int? Year = null,
    string? ImdbId = null,
    int? TmdbId = null,
    int? TvdbId = null,
    IReadOnlyCollection<int>? Categories = null);

/// <summary>One configured tracker: the definition, the address chosen, and what the user filled in.</summary>
public sealed record TrackerConnection(
    string IndexerId,
    TrackerDefinition Definition,
    string BaseUrl,
    IReadOnlyDictionary<string, string?> Settings)
{
    /// <summary>
    /// This indexer's own assigned HTTP/SOCKS proxy (#748, #976), if it has
    /// one: routes this connection's own requests -- search, RSS, grab --
    /// directly, not only the Cloudflare helper's nested request.
    /// </summary>
    public Deluno.Connections.Contracts.ProxyItem? Proxy { get; init; }

    /// <summary>
    /// The same assigned proxy, resolved and ready for the Cloudflare
    /// helper's <c>proxy</c> parameter (#749); null when the indexer has no
    /// proxy assigned, or its assigned proxy is a FlareSolverr/Byparr
    /// instance rather than an HTTP/SOCKS one (that kind is a helper of its
    /// own -- see <see cref="HelperUrl"/> -- not a forward proxy to nest
    /// another helper's solve behind).
    /// </summary>
    public CloudflareHelperProxy? HelperProxy { get; init; }

    /// <summary>
    /// This indexer's own FlareSolverr/Byparr proxy (#976), used as its
    /// Cloudflare helper in place of the single instance-wide helper URL in
    /// Settings; null when the indexer has no such proxy assigned.
    /// </summary>
    public string? HelperUrl { get; init; }
}

/// <summary>A release made ready for a download client: a magnet, or a .torrent file's bytes.</summary>
public sealed record TrackerGrab(string? MagnetUrl, byte[]? TorrentFile, string SourceUrl);

public interface ITrackerEngine
{
    Task<TrackerSearchResult> SearchAsync(TrackerConnection connection, TrackerQuery query, CancellationToken cancellationToken);

    /// <summary>Follows a release's link to what a client can take: signs in, reads the download page, fetches the file.</summary>
    Task<TrackerGrab> ResolveDownloadAsync(TrackerConnection connection, string link, CancellationToken cancellationToken);
}

/// <summary>The tracker said no, or could not be reached; the message says which, in plain words.</summary>
public class TrackerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Searches a tracker from its definition (#606): signs in, builds each
/// search request, reads the pages, and turns a release link into a magnet
/// or a .torrent a client can take. This is Deluno's own tracker engine, so
/// a torrent tracker needs no outside indexer manager any more.
///
/// <para>Each configured tracker has one session: a cookie jar, whether it is
/// signed in, which mirror answered last, and when it was last asked, so the
/// definition's request delay holds across searches.</para>
/// </summary>
public sealed partial class TrackerEngine : ITrackerEngine
{
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private readonly ConcurrentDictionary<string, TrackerSession> sessions = new(StringComparer.Ordinal);
    private readonly Func<CookieContainer, IWebProxy?, HttpMessageHandler> handlerFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TrackerEngine> logger;
    private readonly ICloudflareHelper? cloudflareHelper;

    static TrackerEngine()
    {
        // Windows-1251, ISO-8859-2 and the rest of the code pages trackers use.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public TrackerEngine(ILogger<TrackerEngine> logger, ICloudflareHelper? cloudflareHelper = null)
        : this(DefaultHandler, TimeProvider.System, logger, cloudflareHelper)
    {
    }

    public TrackerEngine(
        Func<CookieContainer, IWebProxy?, HttpMessageHandler> handlerFactory,
        TimeProvider timeProvider,
        ILogger<TrackerEngine> logger,
        ICloudflareHelper? cloudflareHelper = null)
    {
        this.handlerFactory = handlerFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.cloudflareHelper = cloudflareHelper;
    }

    /// <summary>
    /// This connection's own HTTP/SOCKS proxy (#976), when it has one, so a
    /// tracker's own search, RSS and grab requests go out through the proxy
    /// assigned to it -- not only the Cloudflare helper's nested request.
    /// </summary>
    private static HttpMessageHandler DefaultHandler(CookieContainer cookies, IWebProxy? proxy) => new SocketsHttpHandler
    {
        CookieContainer = cookies,
        UseCookies = true,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        // A tracker's proxy is Deluno's own, explicit choice (#748/#749/
        // #976): the indexer's own assigned proxy, passed in as <paramref
        // name="proxy"/> and set directly below, never an ambient
        // HTTP_PROXY/HTTPS_PROXY environment variable or WinINET/WinHTTP
        // system setting left over on the host. Honouring one of those meant
        // a tracker request could be silently rerouted through an address
        // nothing listens on any more, and refused there instead of ever
        // reaching the tracker (#1027) - a leftover a person checking the OS
        // proxy settings alone would never find. UseProxy is only ever true
        // when this connection has its own assigned proxy.
        Proxy = proxy,
        UseProxy = proxy is not null
    };

    public async Task<TrackerSearchResult> SearchAsync(TrackerConnection connection, TrackerQuery query, CancellationToken cancellationToken)
    {
        var definition = connection.Definition;
        var session = SessionFor(connection);
        var releases = new List<TrackerRelease>();
        var problems = new List<string>();
        var skipped = 0;
        Exception? lastFailure = null;
        string? lastFailureLink = null;
        var answered = 0;
        var tried = 0;

        foreach (var siteLink in session.CandidateLinks())
        {
            tried++;
            var variables = TrackerVariables.Build(connection, query, siteLink);
            var paths = definition.Search.Paths.Where(path => AppliesTo(path, variables)).ToArray();
            try
            {
                await EnsureSignedInAsync(session, connection, siteLink, cancellationToken);
                foreach (var path in paths)
                {
                    var request = BuildSearchRequest(definition, path, variables, siteLink);
                    var page = await SendAsync(session, request, cancellationToken);
                    if (definition.Login is not null && NeedsSignIn(definition, request.Uri, page))
                    {
                        // Signed out since the last search: once more, signed in.
                        session.SignedIn = false;
                        await EnsureSignedInAsync(session, connection, siteLink, cancellationToken);
                        page = await SendAsync(session, BuildSearchRequest(definition, path, variables, siteLink), cancellationToken);
                    }

                    FailOnBlockedOrError(page);
                    var parsed = TrackerResponseParser.Parse(definition, path, page.Body, page.FinalUri, variables, Context());
                    releases.AddRange(parsed.Releases);
                    problems.AddRange(parsed.Problems);
                    skipped += parsed.RowsSkipped;
                    answered++;
                }

                session.ActiveLink = siteLink;
                break;
            }
            catch (Exception exception) when (IsMirrorFailure(exception) && !cancellationToken.IsCancellationRequested)
            {
                // This mirror is down or blocked; the next may not be.
                lastFailure = exception;
                lastFailureLink = siteLink;
                session.SignedIn = false;
                logger.LogInformation("Tracker {Tracker} did not answer at {Link}: {Reason}", definition.Name, siteLink, exception.Message);
            }
        }

        if (answered == 0 && lastFailure is not null)
        {
            var described = Describe(lastFailure, lastFailureLink);
            throw new TrackerException(
                tried > 1 ? $"None of the {tried} addresses for {definition.Name} answered. The last: {described}" : described,
                lastFailure);
        }

        if (skipped > 0)
        {
            logger.LogDebug("Tracker {Tracker} skipped {Skipped} rows it could not read: {Problems}", definition.Name, skipped, string.Join("; ", problems));
        }

        return new TrackerSearchResult(Deduplicate(releases), problems, skipped);
    }

    public async Task<TrackerGrab> ResolveDownloadAsync(TrackerConnection connection, string link, CancellationToken cancellationToken)
    {
        if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new TrackerGrab(link, null, link);
        }

        var definition = connection.Definition;
        var session = SessionFor(connection);
        var siteLink = session.ActiveLink ?? session.CandidateLinks().First();
        await EnsureSignedInAsync(session, connection, siteLink, cancellationToken);

        var linkUri = new Uri(link);
        var variables = TrackerVariables.Build(connection, new TrackerQuery(string.Empty), siteLink);
        AddUriVariables(variables, ".DownloadUri", linkUri);
        var context = Context();
        var download = definition.Download;
        var headers = download?.Headers ?? [];

        IElement? beforeRoot = null;
        if (download?.Before is { } before)
        {
            var beforePath = before.Path is null ? link : TrackerTemplate.Render(before.Path, variables);
            var beforeUri = new Uri(linkUri, beforePath);
            var inputs = before.Inputs.Select(input => new KeyValuePair<string, string>(input.Key, TrackerTemplate.Render(input.Value, variables))).ToArray();
            var beforePage = await SendAsync(session, BuildRequest(definition, before.Method, beforeUri, inputs, before.QuerySeparator, headers, variables), cancellationToken);
            beforeRoot = new HtmlParser().ParseDocument(beforePage.Body).DocumentElement;
        }

        if (download is { InfoHash: { } hashSelector })
        {
            var page = await SendAsync(session, BuildRequest(definition, download.Method, linkUri, [], null, headers, variables), cancellationToken);
            var root = new HtmlParser().ParseDocument(page.Body).DocumentElement;
            var hash = TrackerResponseParser.HtmlValue(hashSelector, hashSelector.UseBeforeResponse && beforeRoot is not null ? beforeRoot : root, variables, context, required: true);
            var title = download.InfoHashTitle is { } titleSelector
                ? TrackerResponseParser.HtmlValue(titleSelector, titleSelector.UseBeforeResponse && beforeRoot is not null ? beforeRoot : root, variables, context, required: false)
                : null;
            return new TrackerGrab(TrackerMagnet.Build(hash!, title), null, link);
        }

        if (download is { Selectors.Count: > 0 })
        {
            var page = await SendAsync(session, BuildRequest(definition, download.Method, linkUri, [], null, headers, variables), cancellationToken);
            FailOnBlockedOrError(page);
            var root = new HtmlParser().ParseDocument(page.Body).DocumentElement;
            foreach (var selector in download.Selectors)
            {
                string? href;
                try
                {
                    href = TrackerResponseParser.HtmlValue(selector, selector.UseBeforeResponse && beforeRoot is not null ? beforeRoot : root, variables, context, required: false);
                }
                catch (Exception exception) when (exception is TrackerFieldException or FormatException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var target = TrackerResponseParser.Resolve(page.FinalUri, href);
                if (target.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    return new TrackerGrab(target, null, link);
                }

                var file = await FetchTorrentAsync(session, definition, new Uri(target), headers, variables, cancellationToken);
                if (file is not null)
                {
                    return file with { SourceUrl = link };
                }

                // A selector whose link is not a torrent: the next one may be.
                logger.LogDebug("Tracker {Tracker}: {Target} was not a torrent; trying the next download link.", definition.Name, target);
            }

            throw new TrackerException($"{definition.Name}'s download page had no working download link.");
        }

        return await FetchTorrentAsync(session, definition, linkUri, headers, variables, cancellationToken)
               ?? throw new TrackerException($"{definition.Name} sent a web page instead of a torrent. Its sign-in may have expired.");
    }

    // ---- requests ----------------------------------------------------------

    private sealed record TrackerRequest(HttpMethod Method, Uri Uri, HttpContent? Content, IReadOnlyList<KeyValuePair<string, string>> Headers);

    private sealed record TrackerPage(HttpStatusCode Status, Uri FinalUri, string Body, byte[] Bytes, string? Location, string? MediaType);

    private static bool AppliesTo(TrackerSearchPath path, IReadOnlyDictionary<string, object?> variables)
    {
        if (path.Categories.Count == 0)
        {
            return true;
        }

        var invert = path.Categories[0] == "!";
        var wanted = variables.GetValueOrDefault(".Categories") as IReadOnlyList<string> ?? [];
        var intersects = path.Categories.Skip(invert ? 1 : 0).Any(category => wanted.Contains(category, StringComparer.Ordinal));
        return invert ? !intersects : intersects;
    }

    private TrackerRequest BuildSearchRequest(TrackerDefinition definition, TrackerSearchPath path, IReadOnlyDictionary<string, object?> variables, string siteLink)
    {
        var encoding = EncodingOf(definition);
        var address = TrackerTemplate.Render(path.Path, variables, value => HttpUtility.UrlEncode(value, encoding));
        var uri = new Uri(new Uri(siteLink), address);

        var inputs = new List<KeyValuePair<string, string>>();
        var raw = new List<string>();
        var inputSets = path.InheritInputs ? [definition.Search.Inputs, path.Inputs] : new[] { path.Inputs };
        foreach (var set in inputSets)
        {
            foreach (var (key, value) in set)
            {
                if (key == "$raw")
                {
                    var rendered = TrackerTemplate.Render(value, variables, text => HttpUtility.UrlEncode(text, encoding));
                    raw.AddRange(rendered.Split('&', StringSplitOptions.RemoveEmptyEntries).Where(part => !part.StartsWith('=')));
                    continue;
                }

                var renderedValue = TrackerTemplate.Render(value, variables);
                if (renderedValue.Length == 0 && !definition.Search.AllowEmptyInputs)
                {
                    continue;
                }

                inputs.Add(new(key, renderedValue));
            }
        }

        var method = TrackerTemplate.Render(path.Method, variables);
        var headers = RenderHeaders(definition.Search.Headers, variables);
        return BuildRequest(definition, method, uri, inputs, path.QuerySeparator, headers, raw);
    }

    private TrackerRequest BuildRequest(
        TrackerDefinition definition,
        string? method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> inputs,
        string? separator,
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> headers,
        IReadOnlyDictionary<string, object?> variables)
        => BuildRequest(definition, TrackerTemplate.Render(method, variables), uri, inputs, separator, RenderHeaders(headers, variables), []);

    private static TrackerRequest BuildRequest(
        TrackerDefinition definition,
        string? method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> inputs,
        string? separator,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        IReadOnlyList<string> raw)
    {
        var encoding = EncodingOf(definition);
        var encoded = inputs.Select(input => HttpUtility.UrlEncode(input.Key, encoding) + "=" + HttpUtility.UrlEncode(input.Value, encoding)).Concat(raw).ToArray();
        if (string.Equals(method, "post", StringComparison.OrdinalIgnoreCase))
        {
            var content = new StringContent(string.Join("&", encoded), encoding);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            return new TrackerRequest(HttpMethod.Post, uri, content, headers);
        }

        if (encoded.Length == 0)
        {
            return new TrackerRequest(HttpMethod.Get, uri, null, headers);
        }

        var joined = string.Join(separator ?? "&", encoded);
        var builder = new UriBuilder(uri);
        builder.Query = string.IsNullOrEmpty(builder.Query) || builder.Query == "?" ? joined : builder.Query.TrimStart('?') + (separator ?? "&") + joined;
        return new TrackerRequest(HttpMethod.Get, builder.Uri, null, headers);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> RenderHeaders(
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> headers,
        IReadOnlyDictionary<string, object?> variables)
        => headers.SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, TrackerTemplate.Render(value, variables))))
            .Where(header => header.Value.Length > 0)
            .ToArray();

    /// <summary>
    /// One request, and, when the tracker answers it with a Cloudflare check and
    /// a helper is set up, the way past it (#628). The helper fetches the page;
    /// its clearance cookies and User-Agent go into this tracker's session, and
    /// the request is tried again directly, so later ones need no helper until
    /// the clearance runs out. Cloudflare can still recognise a connection that
    /// is not a browser, and then the page the helper fetched is used.
    /// </summary>
    private async Task<TrackerPage> SendAsync(TrackerSession session, TrackerRequest request, CancellationToken cancellationToken)
    {
        var page = await SendOnceAsync(session, request, cancellationToken);
        if (!IsCloudflare(page) || cloudflareHelper is null || request.Method != HttpMethod.Get)
        {
            return page;
        }

        var solved = await cloudflareHelper.FetchAsync(request.Uri, cancellationToken, session.HelperProxy, session.HelperUrl);
        if (solved is null || IsCloudflareBody(solved.Html))
        {
            logger.LogInformation("The Cloudflare helper could not get past {Host}.", request.Uri.Host);
            return page;
        }

        session.AcceptClearance(solved.Cookies, solved.UserAgent);
        var retried = await SendOnceAsync(session, request, cancellationToken);
        if (!IsCloudflare(retried))
        {
            return retried;
        }

        return new TrackerPage(solved.Status, solved.Url, solved.Html, Encoding.UTF8.GetBytes(solved.Html), null, "text/html");
    }

    private async Task<TrackerPage> SendOnceAsync(TrackerSession session, TrackerRequest request, CancellationToken cancellationToken)
    {
        await session.PaceAsync(timeProvider, cancellationToken);
        using var message = new HttpRequestMessage(request.Method, request.Uri) { Content = request.Content };
        // The helper's own when it cleared a Cloudflare check: the clearance holds for that one only.
        message.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent ?? BrowserUserAgent);
        message.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
        message.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.Remove(name);
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await session.Http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var encoding = ResponseEncoding(response.Content.Headers.ContentType?.CharSet, bytes, session.Encoding);
        return new TrackerPage(
            response.StatusCode,
            response.RequestMessage?.RequestUri ?? request.Uri,
            encoding.GetString(bytes),
            bytes,
            response.Headers.Location?.OriginalString,
            response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<TrackerGrab?> FetchTorrentAsync(
        TrackerSession session,
        TrackerDefinition definition,
        Uri target,
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> headers,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        var page = await SendAsync(session, BuildRequest(definition, null, target, [], null, headers, variables), cancellationToken);
        if (page.Location is { } location && location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new TrackerGrab(location, null, target.AbsoluteUri);
        }

        // A bencoded dictionary starts with 'd'; anything else is a page.
        return (int)page.Status < 400 && page.Bytes.Length > 0 && page.Bytes[0] == (byte)'d'
            ? new TrackerGrab(null, page.Bytes, target.AbsoluteUri)
            : null;
    }

    // ---- signing in --------------------------------------------------------

    private async Task EnsureSignedInAsync(TrackerSession session, TrackerConnection connection, string siteLink, CancellationToken cancellationToken)
    {
        var login = connection.Definition.Login;
        if (login is null || session.SignedIn)
        {
            return;
        }

        await session.SignInLock.WaitAsync(cancellationToken);
        try
        {
            if (session.SignedIn)
            {
                return;
            }

            await SignInAsync(session, connection, login, siteLink, cancellationToken);
            session.SignedIn = true;
        }
        finally
        {
            session.SignInLock.Release();
        }
    }

    private async Task SignInAsync(TrackerSession session, TrackerConnection connection, TrackerLogin login, string siteLink, CancellationToken cancellationToken)
    {
        var definition = connection.Definition;
        var variables = TrackerVariables.Build(connection, new TrackerQuery(string.Empty), siteLink);
        var context = Context();
        var site = new Uri(siteLink);
        var headers = RenderHeaders(login.Headers, variables);
        var loginUri = new Uri(site, TrackerTemplate.Render(login.Path ?? string.Empty, variables));

        foreach (var cookie in login.Cookies)
        {
            session.SetCookies(site, cookie);
        }

        TrackerPage? result = null;
        switch (login.Method?.ToLowerInvariant())
        {
            case "cookie":
                var cookieString = connection.Settings.GetValueOrDefault("cookie");
                if (string.IsNullOrWhiteSpace(cookieString))
                {
                    throw new TrackerException($"{definition.Name} signs in with a cookie; paste one from a browser where you are signed in.");
                }

                session.SetCookies(site, cookieString);
                break;

            case "get":
            {
                var inputs = login.Inputs.Select(input => new KeyValuePair<string, string>(input.Key, TrackerTemplate.Render(input.Value, variables))).ToArray();
                result = await SendAsync(session, BuildRequest(definition, "get", loginUri, inputs, null, headers, []), cancellationToken);
                break;
            }

            case "oneurl":
            {
                var oneUrl = TrackerTemplate.Render(login.Inputs.FirstOrDefault(input => input.Key == "oneurl").Value, variables);
                result = await SendAsync(session, BuildRequest(definition, "get", new Uri(site, (login.Path ?? string.Empty) + oneUrl), [], null, headers, []), cancellationToken);
                break;
            }

            case "form":
            {
                var landing = await SendAsync(session, BuildRequest(definition, "get", loginUri, [], null, headers, []), cancellationToken);
                FailOnBlockedOrError(landing);
                var root = new HtmlParser().ParseDocument(landing.Body).DocumentElement;
                var form = root.QuerySelector(login.Form ?? "form")
                           ?? throw new TrackerException($"{definition.Name}'s sign-in page has no form. The site may have changed.");

                var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var input in form.QuerySelectorAll("input"))
                {
                    if (input.GetAttribute("name") is { Length: > 0 } name)
                    {
                        pairs[name] = input.GetAttribute("value") ?? string.Empty;
                    }
                }

                foreach (var (name, selector) in login.SelectorInputs)
                {
                    pairs[name] = TrackerResponseParser.HtmlValue(selector, root, variables, context, required: true) ?? string.Empty;
                }

                foreach (var (name, value) in login.Inputs)
                {
                    pairs[name] = TrackerTemplate.Render(value, variables);
                }

                var action = login.SubmitPath is { } submitPath ? TrackerTemplate.Render(submitPath, variables) : form.GetAttribute("action") ?? string.Empty;
                var submitUri = new Uri(landing.FinalUri, action.Length == 0 ? landing.FinalUri.AbsoluteUri : action);
                var query = login.GetSelectorInputs
                    .Select(input => new KeyValuePair<string, string>(input.Key, TrackerResponseParser.HtmlValue(input.Value, root, variables, context, required: true) ?? string.Empty))
                    .ToArray();
                if (query.Length > 0)
                {
                    submitUri = BuildRequest(definition, "get", submitUri, query, null, [], []).Uri;
                }

                var formHeaders = headers.Append(new("Referer", landing.FinalUri.AbsoluteUri)).ToArray();
                result = await SendAsync(session, BuildRequest(definition, "post", submitUri, pairs.ToArray(), null, formHeaders, []), cancellationToken);
                break;
            }

            default:
            {
                var inputs = login.Inputs.Select(input => new KeyValuePair<string, string>(input.Key, TrackerTemplate.Render(input.Value, variables))).ToArray();
                result = await SendAsync(session, BuildRequest(definition, "post", loginUri, inputs, null, headers, []), cancellationToken);
                break;
            }
        }

        if (result is not null)
        {
            FailOnBlockedOrError(result, allowClientErrors: true);
            var root = new HtmlParser().ParseDocument(result.Body).DocumentElement;
            foreach (var error in login.Errors)
            {
                if (error.Path is { } errorPath && !result.FinalUri.AbsoluteUri.Contains(errorPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (root.QuerySelector(error.Selector) is { } found)
                {
                    var message = error.Message is null
                        ? TrackerResponseParser.NormalizeSpace(found.TextContent)
                        : TrackerResponseParser.HtmlValue(error.Message, root, variables, context, required: false);
                    throw new TrackerException($"{definition.Name} refused the sign-in: {(string.IsNullOrWhiteSpace(message) ? "no reason given" : message)}");
                }
            }
        }

        if (login.Test is { } test)
        {
            var testUri = new Uri(site, TrackerTemplate.Render(test.Path ?? string.Empty, variables));
            var page = await SendAsync(session, BuildRequest(definition, "get", testUri, [], null, headers, []), cancellationToken);
            if (NeedsSignIn(definition, testUri, page))
            {
                throw new TrackerException(login.Method == "cookie"
                    ? $"{definition.Name} did not accept the cookie. Copy a fresh one from a signed-in browser."
                    : $"{definition.Name} did not accept the sign-in. Check the username and password.");
            }
        }
    }

    /// <summary>Whether a page is the tracker asking Deluno to sign in rather than the page asked for.</summary>
    private static bool NeedsSignIn(TrackerDefinition definition, Uri requested, TrackerPage page)
    {
        var login = definition.Login!;
        if (page.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !IsCloudflare(page))
        {
            return true;
        }

        var loginPath = login.Path is { Length: > 0 } path && !path.Contains("{{", StringComparison.Ordinal)
            ? "/" + path.TrimStart('/').Split('?')[0]
            : null;
        if (!string.Equals(requested.AbsolutePath, page.FinalUri.AbsolutePath, StringComparison.OrdinalIgnoreCase)
            && (loginPath is { Length: > 1 } && page.FinalUri.AbsolutePath.StartsWith(loginPath, StringComparison.OrdinalIgnoreCase)
                || page.FinalUri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (login.Test?.Selector is { Length: > 0 } selector && page.MediaType is null or "text/html")
        {
            var root = new HtmlParser().ParseDocument(page.Body).DocumentElement;
            try
            {
                return root.QuerySelector(selector) is null;
            }
            catch (DomException)
            {
                return false;
            }
        }

        return false;
    }

    // ---- failures ----------------------------------------------------------

    private static void FailOnBlockedOrError(TrackerPage page, bool allowClientErrors = false)
    {
        if (IsCloudflare(page))
        {
            throw new TrackerBlockedException($"{page.FinalUri.Host} is behind a Cloudflare check that turns automated searches away. A Cloudflare helper (FlareSolverr or Byparr), set in Settings, gets Deluno past it; if one is set, it could not get through either.");
        }

        var status = (int)page.Status;
        if (status >= 500)
        {
            throw new TrackerBlockedException($"{page.FinalUri.Host} answered HTTP {status}.");
        }

        if (status >= 400 && !allowClientErrors)
        {
            throw new TrackerException($"{page.FinalUri.Host} answered HTTP {status}.");
        }
    }

    private static bool IsCloudflare(TrackerPage page)
        => (int)page.Status is 403 or 503 or 429 && IsCloudflareBody(page.Body);

    private static bool IsCloudflareBody(string body)
        => body.Contains("cf-chl", StringComparison.Ordinal)
           || body.Contains("challenge-platform", StringComparison.Ordinal)
           || body.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase)
           || body.Contains("Attention Required! | Cloudflare", StringComparison.Ordinal);

    private static bool IsMirrorFailure(Exception exception)
        => exception is HttpRequestException or TrackerBlockedException or TaskCanceledException;

    /// <summary>
    /// What went wrong reaching one mirror, in words that name the address
    /// that actually refused it rather than always blaming "the tracker"
    /// (#1027) — the two are not the same thing: a socket refusal is
    /// Deluno's own request failing to reach <paramref name="address"/> at
    /// all, before the tracker (or a Cloudflare check in front of it) ever
    /// saw it.
    /// </summary>
    private static string Describe(Exception exception, string? address)
    {
        var host = address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
        return exception switch
        {
            TaskCanceledException => host is null ? "The tracker did not answer in time." : $"{host} did not answer in time.",
            HttpRequestException http when http.InnerException is System.Net.Sockets.SocketException socket =>
                host is null
                    ? $"The tracker could not be reached: {socket.Message}"
                    : $"{host} could not be reached: {socket.Message}",
            _ => exception.Message
        };
    }

    // ---- helpers -----------------------------------------------------------

    private TrackerContext Context() => new(timeProvider.GetUtcNow());

    private static Encoding EncodingOf(TrackerDefinition definition)
    {
        try
        {
            return Encoding.GetEncoding(definition.Encoding);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// What a response's own bytes say to decode them as (#632). A response
    /// that declares its charset, in the Content-Type header or an HTML meta
    /// charset, is decoded as that; a tracker definition's configured
    /// encoding is only a fallback for a response that declares neither.
    /// LimeTorrents' pages, and Deluno's definition for it, both already say
    /// UTF-8 — the bug was the engine forcing the definition's encoding onto
    /// every response regardless, which mangles Cyrillic (and anything else
    /// outside ASCII) the moment a definition's configured encoding stops
    /// matching what a mirror actually sends.
    /// </summary>
    private static Encoding ResponseEncoding(string? headerCharset, byte[] bytes, Encoding fallback)
        => TryGetEncoding(headerCharset) ?? TryGetEncoding(SniffMetaCharset(bytes)) ?? fallback;

    private static Encoding? TryGetEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The charset an HTML meta tag declares, sniffed the way a browser
    /// does it: read a prefix as Latin-1, which maps every byte straight to
    /// one char and so never throws and never disturbs the tag's all-ASCII
    /// markup, whatever the body's real encoding turns out to be, and look
    /// for a "charset=" inside a meta tag. That covers both the dedicated
    /// meta-charset tag and the older http-equiv Content-Type form, since
    /// both end up with "charset=..." somewhere between "&lt;meta" and the
    /// tag's closing "&gt;".
    /// </summary>
    private static string? SniffMetaCharset(byte[] bytes)
    {
        var prefix = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        var match = MetaCharsetPattern().Match(prefix);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetPattern();

    /// <summary>The same release from two search pages, once. By its link: one details page can list several copies (YTS lists each quality).</summary>
    private static List<TrackerRelease> Deduplicate(List<TrackerRelease> releases)
        => releases
            .GroupBy(release => release.Link ?? release.DetailsUrl ?? release.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private static void AddUriVariables(Dictionary<string, object?> variables, string prefix, Uri uri)
    {
        variables[prefix + ".AbsoluteUri"] = uri.AbsoluteUri;
        variables[prefix + ".AbsolutePath"] = uri.AbsolutePath;
        variables[prefix + ".Scheme"] = uri.Scheme;
        variables[prefix + ".Host"] = uri.Host;
        variables[prefix + ".Port"] = uri.Port.ToString(CultureInfo.InvariantCulture);
        variables[prefix + ".PathAndQuery"] = uri.PathAndQuery;
        variables[prefix + ".Query"] = uri.Query;
        var query = HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in query.AllKeys.OfType<string>())
        {
            variables[prefix + ".Query." + key] = query[key];
        }
    }

    private TrackerSession SessionFor(TrackerConnection connection)
    {
        var fingerprint = TrackerSession.Fingerprint(connection);
        var session = sessions.AddOrUpdate(
            connection.IndexerId,
            _ => new TrackerSession(connection, fingerprint, handlerFactory),
            (_, existing) => existing.FingerprintValue == fingerprint ? existing : new TrackerSession(connection, fingerprint, handlerFactory));
        return session;
    }
}

/// <summary>A mirror that is down, or turns automated requests away; another mirror may not.</summary>
public sealed class TrackerBlockedException(string message) : Exception(message);

/// <summary>One configured tracker's cookies, sign-in, mirror and pace.</summary>
internal sealed class TrackerSession
{
    private readonly CookieContainer cookies = new();
    private readonly SemaphoreSlim paceLock = new(1, 1);
    private readonly TimeSpan delay;
    private readonly IReadOnlyList<string> links;
    private DateTimeOffset lastRequest = DateTimeOffset.MinValue;

    public TrackerSession(TrackerConnection connection, string fingerprint, Func<CookieContainer, IWebProxy?, HttpMessageHandler> handlerFactory)
    {
        FingerprintValue = fingerprint;
        // The indexer's own assigned HTTP/SOCKS proxy (#976) routes this
        // session's own requests directly; a FlareSolverr/Byparr assignment
        // never reaches here (Deluno.Connections.Data.IndexerProxyWebProxy
        // returns null for it -- that kind is HelperUrl below, not a forward
        // proxy a plain request can be routed through).
        Http = new HttpClient(handlerFactory(cookies, Deluno.Connections.Data.IndexerProxyWebProxy.Build(connection.Proxy)), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        HelperProxy = connection.HelperProxy;
        HelperUrl = connection.HelperUrl;
        delay = TimeSpan.FromSeconds(Math.Max(connection.Definition.RequestDelaySeconds ?? 0, 0));
        try
        {
            Encoding = Encoding.GetEncoding(connection.Definition.Encoding);
        }
        catch (ArgumentException)
        {
            Encoding = Encoding.UTF8;
        }

        // The address the user chose first, then the definition's other mirrors.
        links = new[] { connection.BaseUrl }
            .Concat(connection.Definition.Links)
            .Where(link => !string.IsNullOrWhiteSpace(link) && Uri.IsWellFormedUriString(link, UriKind.Absolute))
            .Select(link => link.TrimEnd('/') + "/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string FingerprintValue { get; }

    /// <summary>This tracker's assigned proxy (#748), if any, for the Cloudflare helper to route through (#749).</summary>
    public CloudflareHelperProxy? HelperProxy { get; }

    /// <summary>This tracker's own FlareSolverr/Byparr proxy (#976), used as its Cloudflare helper in place of the instance-wide one.</summary>
    public string? HelperUrl { get; }

    public HttpClient Http { get; }

    public Encoding Encoding { get; }

    public SemaphoreSlim SignInLock { get; } = new(1, 1);

    public bool SignedIn { get; set; }

    public string? ActiveLink { get; set; }

    /// <summary>The mirror that answered last first, then the rest in order.</summary>
    public IEnumerable<string> CandidateLinks()
        => ActiveLink is null ? links : new[] { ActiveLink }.Concat(links.Where(link => link != ActiveLink));

    /// <summary>The User-Agent a Cloudflare clearance was earned with, once there is one (#628).</summary>
    public string? UserAgent { get; private set; }

    /// <summary>A Cloudflare helper's clearance: its cookies, and the User-Agent they hold for.</summary>
    public void AcceptClearance(IEnumerable<Cookie> cleared, string? userAgent)
    {
        foreach (var cookie in cleared)
        {
            try
            {
                cookies.Add(cookie);
            }
            catch (CookieException)
            {
                // A cookie the container will not hold; the rest still count.
            }
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            UserAgent = userAgent;
        }
    }

    public void SetCookies(Uri site, string cookieHeader)
    {
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Length > 0)
            {
                try
                {
                    cookies.Add(site, new Cookie(pair[0].Trim(), pair[1].Trim(), "/"));
                }
                catch (CookieException)
                {
                    // A value a cookie cannot hold; the rest still count.
                }
            }
        }
    }

    public async Task PaceAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await paceLock.WaitAsync(cancellationToken);
        try
        {
            var wait = lastRequest + delay - timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, timeProvider, cancellationToken);
            }

            lastRequest = timeProvider.GetUtcNow();
        }
        finally
        {
            paceLock.Release();
        }
    }

    /// <summary>
    /// A change to the address, any setting, or the assigned proxy (#976)
    /// starts a new session: new cookies, signed out, and a handler built
    /// fresh for whichever proxy now applies.
    /// </summary>
    /// <remarks>Hashed, so a password is not held a second time as a dictionary key.</remarks>
    public static string Fingerprint(TrackerConnection connection)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            connection.Definition.Id + "|" + connection.BaseUrl + "|" + (connection.Proxy?.Id ?? string.Empty) + "|" + (connection.HelperUrl ?? string.Empty) + "|"
            + string.Join("|", connection.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)))));
}
