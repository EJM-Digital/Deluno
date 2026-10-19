using System.Net;
using System.Text.RegularExpressions;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Recorded;

/// <summary>
/// #1237: the loader of recorded real replies, and the rules the library itself keeps. The recordings
/// written here are about the loader, not about any app: they live in a temporary folder.
/// </summary>
public sealed class RecordedRepliesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deluno-recorded-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void Write(string app, string version, string state, string json)
    {
        var folder = Path.Combine(root, app, version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, state + ".json"), json);
    }

    private const string Sample = """
        { "app": "demo", "version": "1.2.3", "state": "queued",
          "exchanges": [
            { "request": { "method": "get", "path": "/api?b=2&a=1", "body": null },
              "reply": { "status": 200, "headers": { "Content-Type": "application/json", "X-Thing": "yes" }, "body": { "slots": [ { "id": "x" } ] } } },
            { "request": { "method": "POST", "path": "/api/add", "bodyText": "name=a" },
              "reply": { "status": 409, "headers": {}, "bodyText": "not json at all" } }
          ] }
        """;

    [Fact]
    public void A_recording_is_loaded_with_its_requests_and_replies()
    {
        Write("demo", "1.2.3", "queued", Sample);

        var recording = RecordedReplies.Load("demo", "1.2.3", "queued", root);

        Assert.Equal(("demo", "1.2.3", "queued"), (recording.App, recording.Version, recording.State));
        Assert.Equal(2, recording.Exchanges.Count);
        var first = recording.Find("/api?");
        Assert.Equal("GET", first.Request.Method);
        Assert.Equal(200, first.Reply.Status);
        Assert.Contains("\"slots\"", first.Reply.Body);
        Assert.Equal("not json at all", recording.Find("add", "post").Reply.Body);
        Assert.Equal("name=a", recording.Find("add").Request.Body);
        Assert.Throws<InvalidOperationException>(() => recording.Single);
    }

    [Fact]
    public async Task A_recorded_reply_becomes_an_http_response_with_its_status_headers_and_body()
    {
        Write("demo", "1.2.3", "queued", Sample);
        var recording = RecordedReplies.Load("demo", "1.2.3", "queued", root);

        using var response = recording.Find("/api?").Reply.ToResponse();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("yes", Assert.Single(response.Headers.GetValues("X-Thing")));
        Assert.Contains("\"slots\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_replay_handler_answers_by_method_and_path_whatever_the_order_of_the_query()
    {
        Write("demo", "1.2.3", "queued", Sample);
        var recording = RecordedReplies.Load("demo", "1.2.3", "queued", root);
        using var client = new HttpClient(recording.ReplayHandler()) { BaseAddress = new Uri("http://localhost/") };

        using var queue = await client.GetAsync("/api?a=1&b=2");
        using var add = await client.PostAsync("/api/add", new StringContent("name=a"));

        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, add.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api?a=other"));
    }

    [Fact]
    public async Task A_recorded_app_that_did_not_answer_fails_the_request_the_way_a_refused_connection_does()
    {
        Write("demo", "1.2.3", "not-answering", """
            { "app": "demo", "version": "1.2.3", "state": "not-answering",
              "exchanges": [ { "request": { "method": "GET", "path": "/api" }, "reply": { "status": 0, "headers": {}, "error": "No connection could be made" } } ] }
            """);
        var recording = RecordedReplies.Load("demo", "1.2.3", "not-answering", root);
        using var client = new HttpClient(recording.ReplayHandler()) { BaseAddress = new Uri("http://localhost/") };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/api"));

        Assert.Contains("No connection", failure.Message);
    }

    [Fact]
    public void A_state_that_was_never_recorded_fails_and_says_how_to_record_it()
    {
        var failure = Assert.Throws<FileNotFoundException>(() => RecordedReplies.Load("demo", "9.9.9", "paused", root));

        Assert.Contains("Invoke-RecordReplies.ps1", failure.Message);
        Assert.Contains("paused", failure.Message);
    }

    [Theory]
    [InlineData("{ \"version\": \"1\", \"state\": \"s\", \"exchanges\": [] }")]
    [InlineData("{ \"app\": \"a\", \"version\": \"1\", \"state\": \"s\", \"exchanges\": [] }")]
    [InlineData("{ \"app\": \"a\", \"version\": \"1\", \"state\": \"s\", \"exchanges\": [ { \"request\": { \"method\": \"GET\", \"path\": \"/\" } } ] }")]
    [InlineData("{ \"app\": \"a\", \"version\": \"1\", \"state\": \"s\", \"exchanges\": [ { \"request\": { \"path\": \"/\" }, \"reply\": { \"status\": 200 } } ] }")]
    [InlineData("{ \"app\": \"a\", \"version\": \"1\", \"state\": \"s\", \"exchanges\": [ { \"request\": { \"method\": \"GET\", \"path\": \"/\" }, \"reply\": { } } ] }")]
    public void A_recording_missing_what_a_test_relies_on_is_refused(string json)
        => Assert.Throws<FormatException>(() => RecordedReplies.Parse(json));

    [Fact]
    public void Versions_are_listed_in_numeric_order_and_the_latest_is_the_highest()
    {
        foreach (var version in new[] { "5.9.0", "5.10.1", "5.2.0" })
        {
            Write("demo", version, "queued", Sample);
        }

        Assert.Equal(["5.2.0", "5.9.0", "5.10.1"], RecordedReplies.Versions("demo", root));
        Assert.Equal("5.10.1", RecordedReplies.LatestVersion("demo", root));
        Assert.Equal(["queued"], RecordedReplies.States("demo", "5.2.0", root));
        Assert.Empty(RecordedReplies.Versions("nothing", root));
        Assert.Throws<FileNotFoundException>(() => RecordedReplies.LatestVersion("nothing", root));
    }

    private const string Rpc = """
        { "app": "demo", "version": "1.0", "state": "listing",
          "exchanges": [
            { "request": { "method": "POST", "path": "/rpc", "body": { "method": "list", "params": [] } },
              "reply": { "status": 200, "headers": {}, "body": { "result": { "torrents": { "abc123": { "name": "Film", "state": "Seeding", "progress": 100 }, "def456": { "name": "Other" } } }, "list": [1, 2, 3] } } },
            { "request": { "method": "POST", "path": "/rpc", "body": { "method": "remove", "params": ["abc123", true] } },
              "reply": { "status": 200, "headers": {}, "body": { "result": true } } }
          ] }
        """;

    [Fact]
    public void A_recorded_body_is_changed_only_where_the_test_says()
    {
        Write("demo", "1.0", "listing", Rpc);
        var reply = RecordedReplies.Load("demo", "1.0", "listing", root).Find("\"list\"").Reply;

        var edited = System.Text.Json.Nodes.JsonNode.Parse(reply.BodyWith(
            ("result.torrents.*.state", "Downloading"),   // * is the first property: a torrent keyed by its hash
            ("result.torrents.def456", null),               // null removes
            ("^list", 2),                                   // ^ keeps the first elements of a list
            ("result.extra", new System.Text.Json.Nodes.JsonArray(1))))!;

        Assert.Equal("Downloading", (string)edited["result"]!["torrents"]!["abc123"]!["state"]!);
        Assert.Equal("Film", (string)edited["result"]!["torrents"]!["abc123"]!["name"]!);
        Assert.Null(edited["result"]!["torrents"]!["def456"]);
        Assert.Equal(2, edited["list"]!.AsArray().Count);
        Assert.Single(edited["result"]!["extra"]!.AsArray());
        Assert.Throws<KeyNotFoundException>(() => reply.BodyWith(("result.nothing.here", 1)));
    }

    private const string Settings = """
        { "app": "demo", "version": "1.0", "state": "settings",
          "exchanges": [
            { "request": { "method": "POST", "path": "/rpc", "body": { "method": "config" } },
              "reply": { "status": 200, "headers": {}, "body": { "result": [ { "Name": "DestDir", "Value": "C:/Completed" }, { "Name": "Category1.DestDir", "Value": "C:/Completed/Movies" } ], "cats": { "scrubbed-movies": { "path": "x" }, "other": { "path": "y" } } } } }
          ] }
        """;

    [Fact]
    public void A_list_of_settings_is_changed_by_the_name_of_the_setting_and_a_scrubbed_name_can_be_renamed()
    {
        Write("demo", "1.0", "settings", Settings);
        var reply = RecordedReplies.Load("demo", "1.0", "settings", root).Single.Reply;

        var edited = System.Text.Json.Nodes.JsonNode.Parse(reply.BodyWith(
            ("result.[Name=Category1.DestDir].Value", ""),   // the dots inside the brackets belong to the name
            (">cats.scrubbed-movies", "deluno-movies"),         // > renames a property
            ("cats.deluno-movies.path", "z")))!;

        Assert.Equal("C:/Completed", (string)edited["result"]![0]!["Value"]!);
        Assert.Equal("", (string)edited["result"]![1]!["Value"]!);
        Assert.Null(edited["cats"]!["scrubbed-movies"]);
        Assert.Equal("z", (string)edited["cats"]!["deluno-movies"]!["path"]!);
        Assert.Equal("y", (string)edited["cats"]!["other"]!["path"]!);
        Assert.Throws<KeyNotFoundException>(() => reply.BodyWith(("result.[Name=Nothing].Value", "x")));
    }

    [Fact]
    public void A_list_at_the_root_can_be_made_longer_and_a_value_can_be_an_explicit_null_rather_than_taken_out()
    {
        Write("demo", "1.0", "listing", """
            { "app": "demo", "version": "1.0", "state": "listing",
              "exchanges": [ { "request": { "method": "GET", "path": "/torrents" }, "reply": { "status": 200, "headers": {}, "body": [ { "name": "a", "size": 5 } ] } } ] }
            """);
        var reply = RecordedReplies.Load("demo", "1.0", "listing", root).Single.Reply;

        var edited = System.Text.Json.Nodes.JsonNode.Parse(reply.BodyWith(("+", 3), ("1.name", "b"), ("0.size", RecordedReplies.JsonNull), ("2.size", null)))!;

        Assert.Equal(3, edited.AsArray().Count);
        Assert.Equal("b", (string)edited[1]!["name"]!);
        Assert.True(edited[0]!.AsObject().ContainsKey("size"));
        Assert.Null(edited[0]!["size"]);
        Assert.False(edited[2]!.AsObject().ContainsKey("size"));
    }

    [Fact]
    public async Task A_recording_is_replayed_in_the_order_it_happened_and_the_last_reply_repeats()
    {
        Write("demo", "1.0", "controls", """
            { "app": "qbittorrent", "version": "1.0", "state": "controls",
              "exchanges": [
                { "request": { "method": "GET", "path": "/api/v2/torrents/info?hashes=a" }, "reply": { "status": 200, "headers": {}, "body": [ { "state": "stoppedDL" } ] } },
                { "request": { "method": "POST", "path": "/api/v2/torrents/start", "bodyText": "hashes=a" }, "reply": { "status": 200, "headers": {}, "bodyText": "" } },
                { "request": { "method": "GET", "path": "/api/v2/torrents/info?hashes=a" }, "reply": { "status": 200, "headers": {}, "body": [ { "state": "downloading" } ] } }
              ] }
            """);
        Write("demo", "1.0", "opening", """
            { "app": "qbittorrent", "version": "1.0", "state": "opening",
              "exchanges": [ { "request": { "method": "POST", "path": "/api/v2/auth/login", "bodyText": "u" }, "reply": { "status": 204, "headers": {}, "bodyText": "" } } ] }
            """);
        using var client = new HttpClient(RecordedReplies.Load("demo", "1.0", "controls", root).Sequence(RecordedReplies.Load("demo", "1.0", "opening", root))) { BaseAddress = new Uri("http://localhost/") };

        async Task<string> Info() => await (await client.GetAsync("/api/v2/torrents/info?hashes=zzz")).Content.ReadAsStringAsync();

        Assert.Contains("stoppedDL", await Info());
        using var login = await client.PostAsync("/api/v2/auth/login", new StringContent("username=x"));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, login.StatusCode);
        using var started = await client.PostAsync("/api/v2/torrents/start", new StringContent("hashes=zzz"));
        Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
        Assert.Contains("downloading", await Info());
        Assert.Contains("downloading", await Info());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api/v2/never-recorded"));
    }

    [Fact]
    public async Task An_app_that_did_not_answer_makes_every_request_fail_as_the_recording_shows()
    {
        Write("demo", "1.0", "down", """
            { "app": "demo", "version": "1.0", "state": "down",
              "exchanges": [ { "request": { "method": "GET", "path": "/x" }, "reply": { "status": 0, "error": "No connection could be made because the target machine actively refused it." } } ] }
            """);
        using var client = new HttpClient(RecordedReplies.Load("demo", "1.0", "down", root).NotAnswering()) { BaseAddress = new Uri("http://localhost/") };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/anything"));
        Assert.Contains("actively refused", failure.Message);
    }

    [Fact]
    public async Task The_operation_handler_answers_a_call_with_the_reply_to_the_same_kind_of_call_whatever_its_ids()
    {
        Write("demo", "1.0", "listing", Rpc);
        var recording = RecordedReplies.Load("demo", "1.0", "listing", root);
        var seen = new List<string>();
        using var client = new HttpClient(RecordedReplies.Handler("transmission", [recording], (request, body) =>
        {
            seen.Add(body);
            return null;
        })) { BaseAddress = new Uri("http://localhost/") };

        using var removed = await client.PostAsync("/rpc", new StringContent("""{"method":"remove","params":["some-other-id", false]}"""));
        using var listed = await client.PostAsync("/rpc", new StringContent("""{"method":"list","params":[1]}"""));

        Assert.True((bool)System.Text.Json.Nodes.JsonNode.Parse(await removed.Content.ReadAsStringAsync())!["result"]!);
        Assert.Contains("\"torrents\"", await listed.Content.ReadAsStringAsync());
        Assert.Equal(2, seen.Count);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("/rpc", new StringContent("""{"method":"never-recorded"}""")));
        Assert.Contains("never-recorded", failure.Message);
    }

    [Theory]
    [InlineData("sabnzbd", "GET", "/api?mode=queue&name=delete&value=abc&del_files=1&output=json&apikey=k", null, "GET mode=queue name=delete")]
    [InlineData("qbittorrent", "POST", "/api/v2/torrents/delete?x=1", "hashes=a", "POST /api/v2/torrents/delete")]
    [InlineData("nzbget", "POST", "/jsonrpc", """{"method":"editqueue","params":["GroupFinalDelete","",[5]],"id":3}""", "POST /jsonrpc editqueue:GroupFinalDelete")]
    [InlineData("deluge", "POST", "/json", """{"method":"core.remove_torrent","params":["h",true],"id":9}""", "POST /json core.remove_torrent")]
    public void A_call_is_known_by_what_it_asks_not_by_its_ids(string app, string method, string pathAndQuery, string? body, string expected)
        => Assert.Equal(expected, RecordedReplies.OperationOf(app, method, pathAndQuery, body));

    [Fact]
    public void The_recorded_folder_can_be_pointed_somewhere_else_to_try_a_fresh_capture()
    {
        Write("demo", "9.9", "listing", Rpc.Replace("\"version\": \"1.0\"", "\"version\": \"9.9\""));
        var before = Environment.GetEnvironmentVariable("DELUNO_RECORDED_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DELUNO_RECORDED_ROOT", root);
            Assert.Equal(root, RecordedReplies.FindRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DELUNO_RECORDED_ROOT", before);
        }
    }

    // The library itself: whatever is committed under tests/fixtures/recorded must be loadable, sit where its
    // own header says, and carry nothing private (the same list as scripts/check-public-exposure.ps1).
    private static readonly (string Name, Regex Pattern)[] Private =
    [
        ("a LAN address on the home network", new Regex("10" + @"\.1\.1\.")),
        ("the NAS name", new Regex("storage" + "-city", RegexOptions.IgnoreCase)),
        ("the rig's machine name", new Regex("WIN-MJCOV" + "DR5C88", RegexOptions.IgnoreCase)),
        ("the workstation's name", new Regex("SPARE" + "-PC", RegexOptions.IgnoreCase)),
        ("a lab password", new Regex("Deluno-Lab-" + "2026")),
        ("an e-mail address", new Regex(@"(?<![\w.%+-])(?!noreply@)[A-Za-z0-9._%+-]+@(?!example\.(test|com))[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")),
        ("a private LAN address", new Regex(@"\b(?!192\.168\.1\.10\b)(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])|192\.168)\.\d{1,3}\.\d{1,3}\b")),
        ("a person's profile folder", new Regex(@"[A-Za-z]:\\{1,2}Users\\{1,2}(?!<|Public\b|Default\b)[^\\""]+", RegexOptions.IgnoreCase))
    ];

    // An empty library yields one blank name so the theory has data; the test then has nothing to check.
    public static IEnumerable<object[]> LibraryFiles()
    {
        var files = Directory.GetFiles(RecordedReplies.Root, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RecordedReplies.Root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        return files.Length == 0 ? [[""]] : files.Select(f => new object[] { f });
    }

    [Fact]
    public void The_library_folder_exists()
        => Assert.True(Directory.Exists(RecordedReplies.Root));

    [Theory]
    [MemberData(nameof(LibraryFiles))]
    public void Every_recording_in_the_library_loads_sits_where_its_header_says_and_holds_nothing_private(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return;
        }

        var parts = relativePath.Split('/');
        Assert.Equal(3, parts.Length);
        var file = Path.Combine(RecordedReplies.Root, parts[0], parts[1], parts[2]);
        var text = File.ReadAllText(file);

        var recording = RecordedReplies.Parse(text, file);

        Assert.Equal(parts[0], recording.App);
        Assert.Equal(parts[1], recording.Version);
        Assert.Equal(Path.GetFileNameWithoutExtension(parts[2]), recording.State);
        foreach (var (name, pattern) in Private)
        {
            Assert.False(pattern.IsMatch(text), $"{relativePath} carries {name}. Run: node scripts/recorded-replies/scrub.mjs tests/fixtures/recorded");
        }
    }
}
