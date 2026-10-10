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
        ("a private LAN address", new Regex(@"\b(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])|192\.168)\.\d{1,3}\.\d{1,3}\b")),
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
        var text = File.ReadAllText(Path.Combine(RecordedReplies.Root, parts[0], parts[1], parts[2]));

        var recording = RecordedReplies.Parse(text, relativePath);

        Assert.Equal(parts[0], recording.App);
        Assert.Equal(parts[1], recording.Version);
        Assert.Equal(Path.GetFileNameWithoutExtension(parts[2]), recording.State);
        foreach (var (name, pattern) in Private)
        {
            Assert.False(pattern.IsMatch(text), $"{relativePath} carries {name}. Run: node scripts/recorded-replies/scrub.mjs tests/fixtures/recorded");
        }
    }
}
