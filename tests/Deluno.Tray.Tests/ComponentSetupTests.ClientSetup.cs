using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1133: a client the picker installs comes up with the Movies and TV categories Deluno routes to, Deluge's Label
/// plugin on, and every client saving to the person's completed and in-progress folders. #1140: each component's
/// progress is reported, and one failing never stops the others.
/// </summary>
public sealed partial class ComponentSetupTests
{
    private static DownloadLayout TestLayout() => new(
        Path.Combine(Path.GetTempPath(), "deluno-layout-" + Guid.NewGuid().ToString("N"), "Completed"),
        Path.Combine(Path.GetTempPath(), "deluno-layout-" + Guid.NewGuid().ToString("N"), "Incomplete"));

    private sealed record Seen(HttpMethod Method, string Path, string Query, string Body, HttpRequestMessage Request);

    /// <summary>Records every request and answers with whatever the test scripts for it.</summary>
    private sealed class ScriptedClient(Func<Seen, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var seen = new Seen(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, body, request);
            Requests.Add(seen);
            var (status, text) = answer(seen);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static DownloadClientConfigurator ConfiguratorFor(ScriptedClient client)
        => new(() => client, TimeSpan.Zero);

    // ---- the categories every client is given --------------------------------------------------

    [Theory]
    [InlineData("qbittorrent", "deluno-movies", "deluno-tv")]
    [InlineData("deluge", "deluno-movies", "deluno-tv")]
    [InlineData("transmission", "Movies", "TV")]
    [InlineData("sabnzbd", "Movies", "TV")]
    [InlineData("nzbget", "Movies", "TV")]
    public void Each_client_has_one_movies_and_tv_category_that_connecting_it_will_ask_for(string kind, string movies, string tv)
    {
        Assert.Equal((movies, tv), InstalledClientCategories.For(kind));
    }

    [Fact]
    public void The_folder_layout_is_one_completed_folder_per_category_and_one_incomplete_folder_per_client()
    {
        var layout = new DownloadLayout(@"C:\Downloads\Completed", @"D:\Incomplete");

        Assert.Equal(@"C:\Downloads\Completed\Movies", layout.CompletedFolder("Movies").Replace('/', '\\'));
        Assert.Equal(@"D:\Incomplete\SABnzbd", layout.IncompleteFolder("SABnzbd").Replace('/', '\\'));
        Assert.Null(layout.Problem());
        Assert.NotNull(DownloadLayout.Default().CompletedRoot);
        Assert.Null(DownloadLayout.Default().Problem());
    }

    [Theory]
    [InlineData("", @"D:\Incomplete")]
    [InlineData(@"Downloads", @"D:\Incomplete")]
    [InlineData(@"C:\Downloads", @"C:\Downloads")]
    [InlineData(@"C:\Downloads", @"C:\Downloads\Incomplete")]
    [InlineData(@"C:\Downloads\Completed", @"C:\Downloads")]
    public void A_layout_that_cannot_work_says_why(string completed, string incomplete)
    {
        Assert.NotNull(new DownloadLayout(completed, incomplete).Problem());
    }

    // ---- qBittorrent -----------------------------------------------------------------------------

    [Fact]
    public async Task Qbittorrent_gets_both_categories_with_a_folder_each_and_its_in_progress_folder()
    {
        var layout = TestLayout();
        var client = new ScriptedClient(seen => (HttpStatusCode.OK, seen.Path.EndsWith("/login", StringComparison.Ordinal) ? "Ok." : string.Empty));

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("qbittorrent"), layout, new BundledComponentCredentials("deluno", "pw"), CancellationToken.None);

        Assert.Null(problem);
        var created = client.Requests.Where(r => r.Path.EndsWith("/createCategory", StringComparison.Ordinal)).Select(r => Uri.UnescapeDataString(r.Body.Replace('+', ' '))).ToList();
        Assert.Contains(created, body => body.Contains("category=deluno-movies") && body.Contains("savePath=" + layout.CompletedFolder("Movies")));
        Assert.Contains(created, body => body.Contains("category=deluno-tv") && body.Contains("savePath=" + layout.CompletedFolder("TV")));
        var preferences = Uri.UnescapeDataString(client.Requests.Single(r => r.Path.EndsWith("/setPreferences", StringComparison.Ordinal)).Body.Replace('+', ' '));
        Assert.Contains("\"temp_path_enabled\":true", preferences);
        // The categories' folders only apply to torrents qBittorrent manages automatically.
        Assert.Contains("\"auto_tmm_enabled\":true", preferences);
        Assert.Contains("\"torrent_changed_tmm_enabled\":true", preferences);
        Assert.Contains("\"category_changed_tmm_enabled\":true", preferences);
        Assert.Contains(layout.IncompleteFolder("qBittorrent").Replace("\\", "\\\\"), preferences);
        Assert.True(client.Requests.All(r => r.Request.Headers.Referrer is not null), "qBittorrent refuses a request with no Referer.");
        Assert.True(Directory.Exists(layout.CompletedFolder("Movies")));
        Assert.True(Directory.Exists(layout.IncompleteFolder("qBittorrent")));
    }

    [Fact]
    public async Task Qbittorrent_with_a_category_already_there_has_its_folder_edited_instead()
    {
        var client = new ScriptedClient(seen => seen.Path.EndsWith("/createCategory", StringComparison.Ordinal)
            ? (HttpStatusCode.Conflict, string.Empty)
            : (HttpStatusCode.OK, "Ok."));

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("qbittorrent"), TestLayout(), new BundledComponentCredentials("deluno", "pw"), CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(2, client.Requests.Count(r => r.Path.EndsWith("/editCategory", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Qbittorrent_without_the_sign_in_the_installer_made_says_so()
    {
        var problem = await ConfiguratorFor(new ScriptedClient(_ => (HttpStatusCode.OK, "Ok."))).ConfigureAsync(Load("qbittorrent"), TestLayout(), null, CancellationToken.None);

        Assert.Contains("qBittorrent", problem);
    }

    [Fact]
    public async Task Qbittorrent_5_answering_a_right_login_with_204_and_no_body_is_set_up()
    {
        var client = new ScriptedClient(seen => seen.Path.EndsWith("/login", StringComparison.Ordinal)
            ? (HttpStatusCode.NoContent, string.Empty)
            : (HttpStatusCode.OK, "5.2.1"));

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("qbittorrent"), TestLayout(), new BundledComponentCredentials("deluno", "pw"), CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(2, client.Requests.Count(r => r.Path.EndsWith("/createCategory", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "Fails.", HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized, "", HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent, "", HttpStatusCode.Forbidden)]
    public async Task Qbittorrent_refusing_the_login_is_still_reported(HttpStatusCode login, string body, HttpStatusCode version)
    {
        var client = new ScriptedClient(seen => seen.Path.EndsWith("/login", StringComparison.Ordinal) ? (login, body) : (seen.Path.EndsWith("/version", StringComparison.Ordinal) ? (version, "") : (HttpStatusCode.OK, "")));

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("qbittorrent"), TestLayout(), new BundledComponentCredentials("deluno", "pw"), CancellationToken.None);

        Assert.Contains("did not accept the sign-in", problem);
        Assert.DoesNotContain(client.Requests, r => r.Path.EndsWith("/createCategory", StringComparison.Ordinal));
    }

    // ---- Transmission ----------------------------------------------------------------------------

    [Fact]
    public async Task Transmission_is_given_the_completed_folder_the_in_progress_folder_and_its_session_token()
    {
        var layout = TestLayout();
        var client = new ScriptedClient(seen => seen.Request.Headers.Contains("X-Transmission-Session-Id")
            ? (HttpStatusCode.OK, """{"result":"success"}""")
            : (HttpStatusCode.Conflict, string.Empty));
        var configurator = new DownloadClientConfigurator(() => new SessionIdHandler(client), TimeSpan.Zero);

        var problem = await configurator.ConfigureAsync(Load("transmission"), layout, null, CancellationToken.None);

        Assert.Null(problem);
        var body = JsonDocument.Parse(client.Requests.Last().Body).RootElement.GetProperty("arguments");
        Assert.Equal(layout.CompletedRoot, body.GetProperty("download-dir").GetString());
        Assert.Equal(layout.IncompleteFolder("Transmission"), body.GetProperty("incomplete-dir").GetString());
        Assert.True(body.GetProperty("incomplete-dir-enabled").GetBoolean());
        Assert.True(Directory.Exists(layout.CompletedFolder("TV")), "Deluno puts the category folder inside Transmission's download folder, so it must exist.");
    }

    /// <summary>Answers the first request with Transmission's 409 and its session id, as the real RPC does.</summary>
    private sealed class SessionIdHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool _issued;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_issued)
            {
                _issued = true;
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.Add("X-Transmission-Session-Id", "abc123");
                return conflict;
            }

            Assert.Equal("abc123", request.Headers.GetValues("X-Transmission-Session-Id").Single());
            return await base.SendAsync(request, cancellationToken);
        }
    }

    // ---- Deluge ----------------------------------------------------------------------------------

    private static (HttpStatusCode, string) DelugeAnswer(Seen seen, bool labelPluginWorks)
    {
        var method = JsonDocument.Parse(seen.Body).RootElement.GetProperty("method").GetString();
        return (HttpStatusCode.OK, method switch
        {
            "auth.login" or "web.connected" => """{"result":true,"error":null,"id":1}""",
            "label.get_labels" when !labelPluginWorks => """{"result":null,"error":{"message":"Unknown method"},"id":1}""",
            "label.get_labels" => """{"result":[],"error":null,"id":1}""",
            _ => """{"result":null,"error":null,"id":1}"""
        });
    }

    [Fact]
    public async Task Deluge_has_its_label_plugin_switched_on_and_a_label_per_category_that_moves_finished_downloads()
    {
        var layout = TestLayout();
        var client = new ScriptedClient(seen => DelugeAnswer(seen, labelPluginWorks: true));

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("deluge"), layout, null, CancellationToken.None);

        Assert.Null(problem);
        var calls = client.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement).ToList();
        string Method(JsonElement call) => call.GetProperty("method").GetString()!;
        Assert.Equal("deluge", calls.First(call => Method(call) == "auth.login").GetProperty("params")[0].GetString());
        Assert.Equal("Label", calls.Single(call => Method(call) == "core.enable_plugin").GetProperty("params")[0].GetString());
        Assert.Equal(new[] { "deluno-movies", "deluno-tv" }, calls.Where(call => Method(call) == "label.add").Select(call => call.GetProperty("params")[0].GetString()).ToArray());
        var movies = calls.Single(call => Method(call) == "label.set_options" && call.GetProperty("params")[0].GetString() == "deluno-movies").GetProperty("params")[1];
        Assert.True(movies.GetProperty("move_completed").GetBoolean());
        Assert.Equal(layout.CompletedFolder("Movies"), movies.GetProperty("move_completed_path").GetString());
        var config = calls.Single(call => Method(call) == "core.set_config").GetProperty("params")[0];
        Assert.Equal(layout.IncompleteFolder("Deluge"), config.GetProperty("download_location").GetString());
    }

    [Fact]
    public async Task Deluge_whose_label_plugin_will_not_switch_on_is_reported_not_called_ready()
    {
        var client = new ScriptedClient(seen => DelugeAnswer(seen, labelPluginWorks: false));

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("deluge"), TestLayout(), null, CancellationToken.None);

        Assert.Contains("Label plugin", problem);
    }

    // ---- SABnzbd ---------------------------------------------------------------------------------

    /// <summary>A SABnzbd that keeps what it is told, and can lose the first category writes the way a first start rewriting its own defaults would.</summary>
    private sealed class FakeSabnzbd(int categoryWritesToLose = 0, bool neverKeepsCategories = false)
    {
        private readonly Dictionary<string, string> _misc = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _categories = new(StringComparer.OrdinalIgnoreCase);
        private int _lost;

        public (HttpStatusCode, string) Answer(Seen seen)
        {
            var query = System.Web.HttpUtility.ParseQueryString(seen.Query);
            switch (query["mode"])
            {
                case "set_config" when query["section"] == "misc":
                    _misc[query["keyword"]!] = query["value"]!;
                    break;
                case "set_config" when query["section"] == "categories":
                    if (neverKeepsCategories || _lost++ < categoryWritesToLose)
                    {
                        break; // answered "ok", kept nothing
                    }

                    _categories[query["name"]!] = query["dir"]!;
                    break;
                case "get_config" when query["section"] == "misc":
                    return (HttpStatusCode.OK, JsonSerializer.Serialize(new { config = new { misc = _misc } }));
                case "get_config" when query["section"] == "categories":
                    return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                    {
                        config = new { categories = _categories.Select(c => new { name = c.Key, dir = c.Value }).ToArray() }
                    }));
            }

            return (HttpStatusCode.OK, """{"status":true}""");
        }
    }

    [Fact]
    public async Task Sabnzbd_gets_its_two_folders_and_a_category_with_a_folder_each()
    {
        var layout = TestLayout();
        var client = new ScriptedClient(new FakeSabnzbd().Answer);

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("sabnzbd"), layout, new BundledComponentCredentials(string.Empty, "the-key"), CancellationToken.None);

        Assert.Null(problem);
        var queries = client.Requests.Select(r => Uri.UnescapeDataString(r.Query)).ToList();
        Assert.All(queries, query => Assert.Contains("apikey=the-key", query));
        Assert.Contains(queries, query => query.Contains("keyword=download_dir") && query.Contains("value=" + layout.IncompleteFolder("SABnzbd")));
        Assert.Contains(queries, query => query.Contains("keyword=complete_dir") && query.Contains("value=" + layout.CompletedRoot));
        Assert.Contains(queries, query => query.Contains("section=categories") && query.Contains("name=Movies") && query.Contains("dir=" + layout.CompletedFolder("Movies")));
        Assert.Contains(queries, query => query.Contains("section=categories") && query.Contains("name=TV") && query.Contains("dir=" + layout.CompletedFolder("TV")));
        Assert.Contains(queries, query => query.Contains("mode=get_config") && query.Contains("section=categories"));
    }

    [Fact]
    public async Task Sabnzbd_that_loses_the_first_category_writes_is_written_again_until_the_folders_hold()
    {
        var layout = TestLayout();
        var log = new RecordingLog();
        var client = new ScriptedClient(new FakeSabnzbd(categoryWritesToLose: 2).Answer);
        var configurator = new DownloadClientConfigurator(() => client, TimeSpan.Zero, log);

        var problem = await configurator.ConfigureAsync(Load("sabnzbd"), layout, new BundledComponentCredentials(string.Empty, "the-key"), CancellationToken.None);

        Assert.Null(problem);
        Assert.True(client.Requests.Count(r => r.Query.Contains("section=categories") && r.Query.Contains("mode=set_config")) > 2, "the lost writes are made again");
        Assert.Contains(log.Lines, line => line.Contains("Movies=" + layout.CompletedFolder("Movies")));
    }

    [Fact]
    public async Task Sabnzbd_that_never_keeps_the_category_folders_is_reported_not_called_set_up()
    {
        var client = new ScriptedClient(new FakeSabnzbd(neverKeepsCategories: true).Answer);

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("sabnzbd"), TestLayout(), new BundledComponentCredentials(string.Empty, "the-key"), CancellationToken.None);

        Assert.Contains("would not keep", problem);
        Assert.Contains("Movies=(no category)", problem);
    }

    private sealed class RecordingLog : IInstallLog
    {
        public List<string> Lines { get; } = [];

        public void Write(string line) => Lines.Add(line);
    }

    [Fact]
    public async Task Sabnzbd_without_its_api_key_says_so_and_changes_nothing()
    {
        var client = new ScriptedClient(_ => (HttpStatusCode.OK, "{}"));

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("sabnzbd"), TestLayout(), null, CancellationToken.None);

        Assert.Contains("API key", problem);
        Assert.Empty(client.Requests);
    }

    // ---- NZBGet ----------------------------------------------------------------------------------

    private const string NzbgetConfig = """
        {"result":[{"Name":"MainDir","Value":"C:\\ProgramData\\NZBGet"},{"Name":"DestDir","Value":"${MainDir}\\dst"},{"Name":"InterDir","Value":""},
        {"Name":"Category1.Name","Value":"Movies"},{"Name":"Category1.DestDir","Value":""},{"Name":"Category2.Name","Value":"Series"},{"Name":"Category2.DestDir","Value":""},
        {"Name":"Category3.Name","Value":"Music"}]}
        """;

    [Fact]
    public async Task Nzbget_keeps_its_movies_category_renames_its_series_category_to_tv_and_saves_to_the_layout()
    {
        var layout = TestLayout();
        string? loaded = null;
        var client = new ScriptedClient(seen =>
        {
            var method = JsonDocument.Parse(seen.Body).RootElement.GetProperty("method").GetString();
            if (method == "saveconfig")
            {
                // What NZBGet has loaded in memory is what was just saved, once it has reloaded.
                loaded = "{\"result\":" + JsonDocument.Parse(seen.Body).RootElement.GetProperty("params")[0].GetRawText() + "}";
            }

            return (HttpStatusCode.OK, method switch
            {
                "config" => loaded ?? NzbgetConfig,
                "saveconfig" => """{"result":true}""",
                "version" => """{"result":"26.3"}""",
                "status" => """{"result":{"DownloadPaused":false,"PostPaused":false,"ScanPaused":false}}""",
                _ => """{"result":true}"""
            });
        });

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("nzbget"), layout, null, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(new[] { "loadconfig", "config", "saveconfig", "reload", "config", "resumedownload", "resumepost", "resumescan", "status", "resumedownload", "resumepost", "resumescan", "status" }, client.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("method").GetString()!).ToArray());
        var saved = JsonDocument.Parse(client.Requests[2].Body).RootElement.GetProperty("params")[0].EnumerateArray()
            .ToDictionary(item => item.GetProperty("Name").GetString()!, item => item.GetProperty("Value").GetString()!);
        Assert.Equal(layout.CompletedRoot, saved["DestDir"]);
        Assert.Equal(layout.IncompleteFolder("NZBGet"), saved["InterDir"]);
        Assert.Equal("Movies", saved["Category1.Name"]);
        Assert.Equal(layout.CompletedFolder("Movies"), saved["Category1.DestDir"]);
        Assert.Equal("TV", saved["Category2.Name"]);
        Assert.Equal(layout.CompletedFolder("TV"), saved["Category2.DestDir"]);
        Assert.Equal("Music", saved["Category3.Name"]);
        Assert.Equal(@"C:\ProgramData\NZBGet", saved["MainDir"]);
        Assert.True(client.Requests.All(r => r.Request.Headers.Authorization?.Scheme == "Basic"));
    }

    // #1257, from NZBGet 26.3's recorded replies: `config` adds ConfigFile, AppBin, AppDir and Version, and a save that carried
    // them made NZBGet pause all downloads. The recorded file (loadconfig) was taken after the bug had damaged it.
    private static readonly string[] NzbgetRuntimeOnly = ["ConfigFile", "AppBin", "AppDir", "Version"];

    private static string NzbgetRecorded(string state, string method)
        => Deluno.Tests.Recorded.RecordedReplies.Nzbget(state).Find(method).Reply.Body!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nzbget_is_saved_without_its_runtime_only_values_whether_or_not_loadconfig_answers(bool loadconfigFails)
    {
        var client = new ScriptedClient(seen =>
        {
            var method = JsonDocument.Parse(seen.Body).RootElement.GetProperty("method").GetString();
            return (HttpStatusCode.OK, method switch
            {
                "loadconfig" when loadconfigFails => """{"error":{"name":"JSONRPCError","code":1,"message":"Invalid procedure"}}""",
                "loadconfig" => NzbgetRecorded("loadconfig", "loadconfig"),
                "config" => NzbgetRecorded("settings", "\"config\""),
                "version" => """{"result":"26.3"}""",
                "status" => """{"result":{"DownloadPaused":false,"PostPaused":false,"ScanPaused":false}}""",
                _ => """{"result":true}"""
            });
        });

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("nzbget"), TestLayout(), null, CancellationToken.None);

        Assert.Null(problem);
        var save = client.Requests.Single(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("method").GetString() == "saveconfig");
        var saved = JsonDocument.Parse(save.Body).RootElement.GetProperty("params")[0].EnumerateArray()
            .Select(item => item.GetProperty("Name").GetString()!).ToArray();
        Assert.Empty(saved.Intersect(NzbgetRuntimeOnly));
        Assert.Equal(143, saved.Length);
    }

    [Fact]
    public async Task A_fresh_nzbget_that_starts_paused_is_resumed_and_checked_before_it_is_called_set_up()
    {
        var paused = true;
        var client = new ScriptedClient(seen =>
        {
            var method = JsonDocument.Parse(seen.Body).RootElement.GetProperty("method").GetString();
            if (method is "resumedownload" or "resumepost" or "resumescan")
            {
                paused = false;
            }

            return (HttpStatusCode.OK, method switch
            {
                "config" => NzbgetConfig,
                "version" => """{"result":"26.3"}""",
                "status" => $$$"""{"result":{"DownloadPaused":{{{(paused ? "true" : "false")}}},"PostPaused":{{{(paused ? "true" : "false")}}},"ScanPaused":false}}""",
                _ => """{"result":true}"""
            });
        });

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("nzbget"), TestLayout(), null, CancellationToken.None);

        Assert.Null(problem);
        Assert.False(paused);
    }

    // Golden path, rc.30.8: NZBGet answers "reload" and keeps serving from the old instance for a moment, then restarts,
    // and a restarted NZBGet starts paused again. Resuming against the old instance was undone by that restart.
    [Fact]
    public async Task An_nzbget_that_restarts_after_the_reload_answer_is_resumed_after_the_restart_and_stays_resumed()
    {
        var paused = true;
        string? saved = null;
        var reloadAnswered = false;
        var configCallsSinceReload = 0;
        var client = new ScriptedClient(seen =>
        {
            var method = JsonDocument.Parse(seen.Body).RootElement.GetProperty("method").GetString();
            switch (method)
            {
                case "saveconfig":
                    saved = "{\"result\":" + JsonDocument.Parse(seen.Body).RootElement.GetProperty("params")[0].GetRawText() + "}";
                    break;
                case "reload":
                    reloadAnswered = true;
                    break;
                case "resumedownload" or "resumepost" or "resumescan":
                    paused = false;
                    break;
                case "config" when reloadAnswered && ++configCallsSinceReload == 3:
                    // The restart lands: the new instance holds the new settings and starts paused.
                    paused = true;
                    break;
            }

            var flag = paused ? "true" : "false";
            return (HttpStatusCode.OK, method switch
            {
                // Until the restart lands the old instance still reports the old settings.
                "config" => reloadAnswered && configCallsSinceReload >= 3 ? saved! : NzbgetConfig,
                "version" => """{"result":"26.3"}""",
                "status" => "{\"result\":{\"DownloadPaused\":" + flag + ",\"PostPaused\":" + flag + ",\"ScanPaused\":false,\"ServerPaused\":" + flag + "}}",
                _ => """{"result":true}"""
            });
        });

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("nzbget"), TestLayout(), null, CancellationToken.None);

        Assert.Null(problem);
        Assert.False(paused);
        var methods = client.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("method").GetString()!).ToList();
        Assert.True(methods.LastIndexOf("resumedownload") > methods.LastIndexOf("reload") + 2, "it must resume after the new instance is up, not against the old one");
    }

    [Fact]
    public async Task An_nzbget_that_stays_paused_is_reported()
    {
        var client = new ScriptedClient(seen =>
        {
            var method = JsonDocument.Parse(seen.Body).RootElement.GetProperty("method").GetString();
            return (HttpStatusCode.OK, method switch
            {
                "config" => NzbgetConfig,
                "version" => """{"result":"26.3"}""",
                "status" => """{"result":{"DownloadPaused":true,"PostPaused":true,"ScanPaused":true}}""",
                _ => """{"result":true}"""
            });
        });

        var problem = await ConfiguratorFor(client).ConfigureAsync(Load("nzbget"), TestLayout(), null, CancellationToken.None);

        Assert.Contains("still paused", problem);
    }

    [Fact]
    public void Nzbget_puts_a_category_that_does_not_exist_in_the_first_free_slot()
    {
        var layout = new DownloadLayout(@"C:\Done", @"C:\Doing");
        var plan = NzbgetSettings.Plan([new("Category1.Name", "Music"), new("Category2.Name", "")], layout, @"C:\Doing\NZBGet")
            .ToDictionary(item => item.Key, item => item.Value);

        Assert.Equal("Music", plan["Category1.Name"]);
        Assert.Equal("Movies", plan["Category2.Name"]);
        Assert.Equal("TV", plan["Category3.Name"]);
    }

    // ---- the install itself: progress, setup, and one failure not stopping the rest ----------------

    private sealed class RecordingConfigurator(string? problem) : IDownloadClientConfigurator
    {
        public List<(string Kind, DownloadLayout Layout)> Calls { get; } = [];

        public Task<string?> ConfigureAsync(BundledComponentSpec spec, DownloadLayout layout, BundledComponentCredentials? credentials, CancellationToken cancellationToken)
        {
            Calls.Add((spec.Kind, layout));
            return Task.FromResult(problem);
        }
    }

    private static ComponentInstallService ServiceWith(IDownloadClientConfigurator configurator, IComponentDownloader? downloader = null)
        => new(new FakePrograms(), new FakePorts(), downloader ?? new FixedDownloader(WriteTempFile(new byte[20], ".exe")), new AlwaysSigned(), new ElevatedRecordingRunner(),
            new RecordingStarter(), new ReadyChecker(), new FakeConfigWriter(true), templates: new FixedTemplate(null),
            credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry(), installedRecord: new NoInstalledComponentRecord(),
            credentialCaptureWait: TimeSpan.Zero, clientConfigurator: configurator);

    private static BundledComponentSpec Runnable(string kind) => Load(kind) with
    {
        Url = "https://example.invalid/installer.exe", GithubRepo = null, DirectoryListingUrl = null,
        Sha256 = null, MustBeSigned = true, ExpectedPublisher = "Test Publisher", MinimumBytes = 1, Prerequisite = null, ReadinessTimeoutSeconds = 1
    };

    [Fact]
    public async Task A_download_client_is_set_up_after_it_starts_and_each_step_is_reported()
    {
        var configurator = new RecordingConfigurator(null);
        var layout = TestLayout();
        var steps = new List<ComponentStage>();

        var result = await ServiceWith(configurator).InstallAsync(Runnable("deluge"), CancellationToken.None, progress => steps.Add(progress.Stage), layout);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.Equal([("deluge", layout)], configurator.Calls);
        Assert.Equal(
            [ComponentStage.Downloading, ComponentStage.Downloading, ComponentStage.Checking, ComponentStage.Installing, ComponentStage.Starting, ComponentStage.SettingUp, ComponentStage.Done],
            steps);
    }

    [Fact]
    public async Task A_client_that_could_not_be_set_up_is_installed_but_says_what_to_do_by_hand()
    {
        var result = await ServiceWith(new RecordingConfigurator("Deluge's Label plugin would not switch on.")).InstallAsync(Runnable("deluge"), CancellationToken.None, null, TestLayout());

        Assert.Equal(ComponentInstallOutcome.InstalledNeedsSetup, result.Outcome);
        Assert.Contains("Label plugin", result.Message);
        Assert.Contains("in Deluge itself", result.Message);
    }

    [Fact]
    public async Task Weir_and_a_client_installed_without_a_layout_are_never_given_categories()
    {
        var configurator = new RecordingConfigurator(null);
        var service = ServiceWith(configurator);

        await service.InstallAsync(Runnable("weir"), CancellationToken.None, null, TestLayout());
        await service.InstallAsync(Runnable("deluge"), CancellationToken.None, null, null);

        Assert.Empty(configurator.Calls);
    }

    [Fact]
    public async Task A_component_that_fails_is_reported_failed_with_its_reason_and_the_next_one_still_installs()
    {
        var steps = new List<ComponentProgress>();
        var service = ServiceWith(new RecordingConfigurator(null), new ThrowingDownloader());

        var failed = await service.InstallAsync(Runnable("deluge"), CancellationToken.None, steps.Add, TestLayout());

        Assert.Equal(ComponentInstallOutcome.InstallerFailed, failed.Outcome);
        Assert.Equal(ComponentStage.Failed, steps[^1].Stage);
        Assert.Contains("network is down", steps[^1].Detail);

        var next = await ServiceWith(new RecordingConfigurator(null)).InstallAsync(Runnable("sabnzbd"), CancellationToken.None, null, TestLayout());
        Assert.Equal(ComponentInstallOutcome.Installed, next.Outcome);
    }

    private sealed class ThrowingDownloader : IComponentDownloader
    {
        public Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken) => throw new InvalidOperationException("network is down");
    }

    [Fact]
    public void The_finish_window_says_what_was_installed_and_what_was_not()
    {
        var specs = new[] { Load("deluge"), Load("nzbget"), Load("sabnzbd") };
        var text = ComponentPickerDialog.Summarise(specs,
        [
            new ComponentInstallResult("deluge", ComponentInstallOutcome.Installed, "Deluge installed."),
            new ComponentInstallResult("nzbget", ComponentInstallOutcome.DownloadFailed, "Could not download NZBGet's installer."),
            new ComponentInstallResult("sabnzbd", ComponentInstallOutcome.InstalledNeedsSetup, "SABnzbd installed and running. It would not take its folders.")
        ]);

        Assert.Contains("Installed: Deluge.", text);
        Assert.Contains("Not installed: NZBGet. Could not download NZBGet's installer.", text);
        Assert.Contains("needs a look: SABnzbd installed and running", text);
        Assert.Contains("Open Deluno", text);
        Assert.Equal("Failed: Could not download it.", ComponentPickerDialog.Describe(new ComponentProgress("x", "X", ComponentStage.Failed, "Could not download it.")));
    }

    [Fact]
    public void Nzbget_installs_from_the_maintained_project_with_its_current_windows_installer_name()
    {
        var nzbget = Load("nzbget");
        var pattern = new System.Text.RegularExpressions.Regex(nzbget.GithubAssetPattern!);

        Assert.Equal("nzbgetcom/nzbget", nzbget.GithubRepo);
        Assert.Matches(pattern, "nzbget-26.3-bin-windows-setup.exe");
        Assert.DoesNotMatch(pattern, "nzbget-26.3-bin-windows-debug-setup.exe");
        Assert.Matches(new System.Text.RegularExpressions.Regex(nzbget.GithubChecksumAssetPattern!), "nzbget-26.3.sig.txt");
    }
}
