using System.Net;
using System.Text;
using System.Text.Json;

namespace Deluno.Tests.Recorded;

/// <summary>
/// Loads the recorded real replies of external apps (#1237) from
/// <c>tests/fixtures/recorded/&lt;app&gt;/&lt;version&gt;/&lt;state&gt;.json</c>. A test of a download client,
/// indexer, media server or processor takes its replies from here, never from a shape written
/// in the test. This one file is linked into every test project that needs it.
/// </summary>
public static class RecordedReplies
{
    /// <summary>The folder holding the library, found by walking up from the test binaries.</summary>
    public static string Root { get; } = FindRoot();

    public static string FindRoot()
    {
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

    /// <summary>The first exchange whose request path (with its query) contains the text, for example "mode=queue".</summary>
    public Exchange Find(string pathContains, string? method = null)
        => Exchanges.FirstOrDefault(e => e.Request.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase)
                && (method is null || e.Request.Method.Equals(method, StringComparison.OrdinalIgnoreCase)))
            ?? throw new KeyNotFoundException($"{App} {Version} {State} has no exchange for '{pathContains}'. It has: {string.Join(", ", Exchanges.Select(e => e.Request.Method + " " + e.Request.Path))}");

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
