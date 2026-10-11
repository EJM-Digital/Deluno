using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Tests.Recorded;

/// <summary>
/// Loads the recorded real replies of external apps (#1237) from
/// <c>tests/fixtures/recorded/&lt;app&gt;/&lt;version&gt;/&lt;state&gt;.json</c>. A test of a download client,
/// indexer, media server or processor takes its replies from here, never from a shape written
/// in the test. This one file is linked into every test project that needs it.
/// </summary>
public static class RecordedReplies
{
    // The newest recorded state of each app, by name: RecordedReplies.Qbittorrent("finished-single-file").
    public static Recording Sabnzbd(string state) => RecordedReplies.LoadLatest("sabnzbd", state);
    public static Recording Nzbget(string state) => RecordedReplies.LoadLatest("nzbget", state);
    public static Recording Qbittorrent(string state) => RecordedReplies.LoadLatest("qbittorrent", state);
    public static Recording Transmission(string state) => RecordedReplies.LoadLatest("transmission", state);
    public static Recording Deluge(string state) => RecordedReplies.LoadLatest("deluge", state);
    public static Recording Weir(string state) => RecordedReplies.LoadLatest("weir", state);
    public static Recording Deluno(string state) => RecordedReplies.LoadLatest("deluno", state);
    /// <summary>A JSON reply as an HTTP 200 response, for the parts of a test that are not an external app's reply.</summary>
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// A handler that answers each call with the recorded reply to the same OPERATION (what is being asked, not which torrent
    /// or job: SABnzbd's mode and name, a JSON-RPC method, qBittorrent's path), taken from the given recordings in order. The
    /// ids in the call are the test's own; the answer is what the real app said to that kind of call. A call no recording
    /// answers fails the test, naming the call. <paramref name="intercept"/> sees every call first (with its body text) and
    /// may answer it itself, for a reply a recording changes with <see cref="RecordedReply.BodyWith"/>.
    /// </summary>
    public static HttpMessageHandler Handler(string app, IEnumerable<Recording> recordings, Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => new OperationHandler(app, recordings.ToArray(), intercept);

    /// <summary>The kind of call a request is, for matching it to a recorded one.</summary>
    public static string OperationOf(string app, string method, string pathAndQuery, string? body)
    {
        var split = pathAndQuery.Split('?', 2);
        var path = split[0];
        var query = System.Web.HttpUtility.ParseQueryString(split.Length > 1 ? split[1] : string.Empty);
        switch (app)
        {
            case "sabnzbd":
                return $"{method} mode={query["mode"]} name={query["name"]}";
            case "nzbget":
            case "transmission":
            case "deluge":
                try
                {
                    using var document = JsonDocument.Parse(body ?? "{}");
                    var root = document.RootElement;
                    var rpc = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                    // NZBGet's editqueue is many calls in one: the command (GroupDelete, HistoryFinalDelete...) is the first parameter.
                    if (app == "nzbget" && rpc == "editqueue" && root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0)
                    {
                        rpc += ":" + p[0].GetString();
                    }

                    return $"{method} {path} {rpc}";
                }
                catch (JsonException)
                {
                    return $"{method} {path} ?";
                }

            default:
                // qBittorrent and the rest: the path says what is asked.
                return $"{method} {path}";
        }
    }

    private sealed class OperationHandler(string app, Recording[] recordings, Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (intercept?.Invoke(request, body) is { } own)
            {
                return own;
            }

            var operation = OperationOf(app, request.Method.Method, request.RequestUri!.PathAndQuery, body);
            foreach (var recording in recordings)
            {
                foreach (var exchange in recording.Exchanges)
                {
                    if (OperationOf(app, exchange.Request.Method, exchange.Request.Path, exchange.Request.Body) == operation)
                    {
                        if (exchange.Reply.Status == 0)
                        {
                            throw new HttpRequestException(exchange.Reply.Error ?? "The connection was refused.");
                        }

                        return exchange.Reply.ToResponse();
                    }
                }
            }

            throw new InvalidOperationException($"No recording ({string.Join(", ", recordings.Select(r => r.App + " " + r.Version + " " + r.State))}) answers this call: {operation}. Record it with scripts/recorded-replies/Invoke-RecordReplies.ps1.");
        }
    }

    /// <summary>The folder holding the library, found by walking up from the test binaries.</summary>
    public static string Root { get; } = FindRoot();

    public static string FindRoot()
    {
        // For trying a fresh capture before it is committed: DELUNO_RECORDED_ROOT names a folder laid out like tests/fixtures/recorded.
        if (Environment.GetEnvironmentVariable("DELUNO_RECORDED_ROOT") is { Length: > 0 } overridden && Directory.Exists(overridden))
        {
            return overridden;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "recorded");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("tests/fixtures/recorded was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Every version recorded for an app, oldest first by version number.</summary>
    public static IReadOnlyList<string> Versions(string app, string? root = null)
    {
        var folder = Path.Combine(root ?? Root, app);
        return !Directory.Exists(folder)
            ? []
            : Directory.GetDirectories(folder).Select(d => Path.GetFileName(d)!).OrderBy(VersionKey, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The newest version recorded for an app. Fails the test, loudly, if none is recorded.</summary>
    public static string LatestVersion(string app, string? root = null)
        => Versions(app, root).LastOrDefault()
            ?? throw new FileNotFoundException($"No recording of {app} exists under {root ?? Root}. Record it with scripts/recorded-replies/Invoke-RecordReplies.ps1.");

    /// <summary>The states recorded for an app at a version.</summary>
    public static IReadOnlyList<string> States(string app, string version, string? root = null)
    {
        var folder = Path.Combine(root ?? Root, app, version);
        return !Directory.Exists(folder)
            ? []
            : Directory.GetFiles(folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    public static bool Exists(string app, string version, string state, string? root = null)
        => File.Exists(PathOf(app, version, state, root));

    /// <summary>Loads one recorded state. A missing recording is a failure that names what to record.</summary>
    public static Recording Load(string app, string version, string state, string? root = null)
    {
        var path = PathOf(app, version, state, root);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No recording of {app} {version} in the state '{state}' ({path}). Record it with scripts/recorded-replies/Invoke-RecordReplies.ps1.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Loads a state from the newest version of the app.</summary>
    public static Recording LoadLatest(string app, string state, string? root = null)
        => Load(app, LatestVersion(app, root), state, root);

    public static Recording Parse(string json, string source = "recording")
    {
        using var document = JsonDocument.Parse(json);
        var rootElement = document.RootElement;
        string Required(string name) => rootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"{source}: '{name}' is missing.");

        var exchanges = new List<Exchange>();
        if (!rootElement.TryGetProperty("exchanges", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            throw new FormatException($"{source}: 'exchanges' must hold at least one request and reply.");
        }

        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("request", out var request) || !item.TryGetProperty("reply", out var reply))
            {
                throw new FormatException($"{source}: every exchange needs a 'request' and a 'reply'.");
            }

            exchanges.Add(new Exchange(ReadRequest(request, source), ReadReply(reply, source)));
        }

        return new Recording(Required("app"), Required("version"), Required("state"), exchanges);
    }

    private static RecordedRequest ReadRequest(JsonElement request, string source)
    {
        var method = request.TryGetProperty("method", out var m) && m.GetString() is { Length: > 0 } verb ? verb : throw new FormatException($"{source}: a request has no method.");
        var path = request.TryGetProperty("path", out var p) && p.GetString() is { } route ? route : throw new FormatException($"{source}: a request has no path.");
        return new RecordedRequest(method.ToUpperInvariant(), path, BodyText(request));
    }

    private static RecordedReply ReadReply(JsonElement reply, string source)
    {
        var status = reply.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : throw new FormatException($"{source}: a reply has no status.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reply.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in h.EnumerateObject())
            {
                headers[header.Name] = header.Value.ToString();
            }
        }

        return new RecordedReply(status, headers, BodyText(reply), reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null);
    }

    /// <summary>A body is stored as JSON in <c>body</c> or as text in <c>bodyText</c> (a reply that is not JSON, or whose exact bytes matter).</summary>
    private static string? BodyText(JsonElement element)
    {
        if (element.TryGetProperty("bodyText", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        return element.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null ? body.GetRawText() : null;
    }

    private static string PathOf(string app, string version, string state, string? root)
        => Path.Combine(root ?? Root, app, version, state + ".json");

    // Versions sort numerically per part so 5.10 comes after 5.9.
    private static string VersionKey(string version)
        => string.Join('.', version.Split('.', '-').Select(part => int.TryParse(part, out var n) ? n.ToString("D6") : part));
}

/// <summary>One recorded state of one app at one version: the requests made and the real replies.</summary>
public sealed record Recording(string App, string Version, string State, IReadOnlyList<Exchange> Exchanges)
{
    /// <summary>The only exchange of a state recorded with one.</summary>
    public Exchange Single => Exchanges.Count == 1 ? Exchanges[0] : throw new InvalidOperationException($"{App} {Version} {State} holds {Exchanges.Count} exchanges; pick one.");

    /// <summary>The first exchange whose request path (with its query) or body contains the text, for example "mode=queue" or "GroupFinalDelete".</summary>
    public Exchange Find(string pathContains, string? method = null)
        => Exchanges.FirstOrDefault(e => (e.Request.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase)
                    || (e.Request.Body?.Contains(pathContains, StringComparison.OrdinalIgnoreCase) ?? false))
                && (method is null || e.Request.Method.Equals(method, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"{App} {Version} {State} has no exchange for '{pathContains}'. It has: {string.Join(", ", Exchanges.Select(e => e.Request.Method + " " + e.Request.Path))}");

    /// <summary>
    /// The recorded reply body of the first exchange whose request contains the text, with the given values changed
    /// (see <see cref="RecordedReply.BodyWith"/>). With no edits it is the body exactly as the app sent it.
    /// </summary>
    public string Body(string pathContains, params (string Path, object? Value)[] edits)
    {
        var reply = Find(pathContains).Reply;
        return edits.Length == 0 ? reply.Body ?? string.Empty : reply.BodyWith(edits);
    }

    /// <summary>The same as an HTTP response, with the recorded status and headers.</summary>
    public HttpResponseMessage Response(string pathContains, params (string Path, object? Value)[] edits)
    {
        var reply = Find(pathContains).Reply;
        var response = reply.ToResponse();
        if (edits.Length > 0)
        {
            response.Content = new StringContent(reply.BodyWith(edits), Encoding.UTF8, "application/json");
        }

        return response;
    }

    /// <summary>
    /// A handler that answers each request with the recorded reply for the same method and path (query included, in
    /// any order of its parameters), and fails the test on a request that was never recorded.
    /// </summary>
    public HttpMessageHandler ReplayHandler(Action<HttpRequestMessage>? onRequest = null) => new Replay(this, onRequest);

    private sealed class Replay(Recording recording, Action<HttpRequestMessage>? onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke(request);
            var wanted = Normalize(request.RequestUri!.PathAndQuery);
            var match = recording.Exchanges.FirstOrDefault(e =>
                e.Request.Method.Equals(request.Method.Method, StringComparison.OrdinalIgnoreCase) && Normalize(e.Request.Path) == wanted);
            if (match is null)
            {
                throw new InvalidOperationException($"{request.Method} {request.RequestUri!.PathAndQuery} was never recorded for {recording.App} {recording.Version} {recording.State}.");
            }

            if (match.Reply.Status == 0)
            {
                // The app did not answer at all: the connection was refused, as the recording shows.
                throw new HttpRequestException(match.Reply.Error ?? "The connection was refused.");
            }

            return Task.FromResult(match.Reply.ToResponse());
        }

        private static string Normalize(string pathAndQuery)
        {
            var split = pathAndQuery.Split('?', 2);
            return split.Length == 1 ? split[0] : split[0] + "?" + string.Join("&", split[1].Split('&', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal));
        }
    }
}

public sealed record Exchange(RecordedRequest Request, RecordedReply Reply);

public sealed record RecordedRequest(string Method, string Path, string? Body);

/// <summary>A reply. Status 0 is an app that did not answer (connection refused); <c>Error</c> says why.</summary>
public sealed record RecordedReply(int Status, IReadOnlyDictionary<string, string> Headers, string? Body, string? Error = null)
{
    public HttpStatusCode StatusCode => (HttpStatusCode)Status;

    /// <summary>The reply as an HTTP response, with the recorded status, headers and body.</summary>
    /// <summary>The body parsed as JSON.</summary>
    public JsonNode Json() => JsonNode.Parse(Body ?? throw new InvalidOperationException("This recorded reply has no body."))!;

    /// <summary>
    /// The recorded body with the values a test chooses changed, everything else exactly as the app sent it. Each path is
    /// dot-separated property names and array positions ("0.state", "result.abc123.progress", "arguments.torrents.0.percentDone");
    /// a null value removes the property. Use it to set the one thing a test is about (a state the VM cannot be put in, a
    /// ratio); never to rebuild a reply, which a test of an external app must not do.
    /// </summary>
    public string BodyWith(params (string Path, object? Value)[] edits)
    {
        var root = Json();
        foreach (var (rawPath, value) in edits)
        {
            // "^queue.slots" with a count keeps only that many elements of the array, as the client would list that many jobs.
            if (rawPath.StartsWith('^'))
            {
                var list = Json2(root, rawPath[1..]) as JsonArray ?? throw new KeyNotFoundException($"The recorded reply has no list at '{rawPath[1..]}'.");
                while (list.Count > Convert.ToInt32(value))
                {
                    list.RemoveAt(list.Count - 1);
                }

                continue;
            }

            // "+list" with a count makes the list that long by copying its first element, for a test about several torrents or jobs
            // (each copy is then changed by the edits that follow).
            if (rawPath.StartsWith('+'))
            {
                var list = Json2(root, rawPath[1..]) as JsonArray ?? throw new KeyNotFoundException($"The recorded reply has no list at '{rawPath[1..]}'.");
                while (list.Count < Convert.ToInt32(value))
                {
                    list.Add(list[0]!.DeepClone());
                }

                continue;
            }

            var path = rawPath;
            JsonNode? node = root;
            var parts = path.Split('.');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                node = Step(node, parts[i]) ?? throw new KeyNotFoundException($"The recorded reply has nothing at '{string.Join('.', parts[..(i + 1)])}' (while setting '{path}').");
            }

            var last = parts[^1];
            switch (node)
            {
                case JsonObject obj when value is null: obj.Remove(last); break;
                case JsonObject obj: obj[last] = Wrap(value); break;
                case JsonArray arr when int.TryParse(last, out var index): arr[index] = Wrap(value); break;
                default: throw new KeyNotFoundException($"Cannot set '{path}' in the recorded reply.");
            }
        }

        return root.ToJsonString();
    }

    private static JsonNode? Json2(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            node = Step(node, part);
        }

        return node;
    }

    private static JsonNode? Wrap(object? value)
        => value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            _ => JsonValue.Create(value)
        };

    private static JsonNode? Step(JsonNode? node, string part)
        => node switch
        {
            JsonObject obj when part == "*" => obj.FirstOrDefault().Value,
            JsonObject obj => obj[part],
            JsonArray arr when int.TryParse(part, out var index) && index < arr.Count => arr[index],
            _ => null
        };

    public HttpResponseMessage ToResponse()
    {
        var response = new HttpResponseMessage(StatusCode);
        var contentType = Headers.TryGetValue("content-type", out var type) ? type : "application/json";
        response.Content = new StringContent(Body ?? string.Empty, Encoding.UTF8);
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        foreach (var (name, value) in Headers)
        {
            if (!name.Equals("content-type", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }
}
