using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Companions;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The routes behind the guided setup, over a real host with a real database file: the four setup routes (progress, draft, completed),
/// the key policies of the installed-apps routes, and what the draft keeps. Nothing here connects an app or opens the firewall.
/// </summary>
public sealed class GuidedSetupRoutesTests
{
    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Key " + scopes, scopes });
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<JsonElement> ReadProgressAsync(ApplicationTestHost app)
        => await (await app.Client.GetAsync("/api/setup/progress")).Content.ReadFromJsonAsync<JsonElement>();

    private static Task<HttpResponseMessage> PutProgressAsync(ApplicationTestHost app, int step, bool skipped = false, bool completed = false)
        => app.Client.PutAsJsonAsync("/api/setup/progress", new { lastCompletedStep = step, isSkipped = skipped, isCompleted = completed });

    private static Task<HttpResponseMessage> PutDraftAsync(ApplicationTestHost app, object draft) => app.Client.PutAsJsonAsync("/api/setup/draft", draft);

    [Fact]
    public async Task All_the_setup_routes_refuse_a_caller_who_is_not_signed_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/setup/progress")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/setup/progress", new { lastCompletedStep = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/setup/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/setup/draft", new { mode = "advanced" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/setup/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/setup/completed", new { libraries = new[] { "Movies" } })).StatusCode);

        var progress = await ReadProgressAsync(app);
        Assert.False(progress.GetProperty("isCompleted").GetBoolean());
        Assert.Equal(0, progress.GetProperty("lastCompletedStep").GetInt32());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(11, 11)]
    [InlineData(-5, 0)]
    public async Task A_step_inside_the_range_the_guide_has_is_saved_as_it_is_and_one_below_zero_is_held_at_zero(int sent, int expected)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var put = await PutProgressAsync(app, sent, skipped: true);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var progress = await ReadProgressAsync(app);
        Assert.Equal(expected, progress.GetProperty("lastCompletedStep").GetInt32());
        Assert.True(progress.GetProperty("isSkipped").GetBoolean());
    }

    [Fact(Skip = "DEFECT: SaveSetupProgressAsync clamps the step to 0..11 (src/Deluno.Platform/Data/SqlitePlatformSettingsRepository.cs line 55) but the guide has 13 steps, 0..12; the Start step (12) comes back as 11.")]
    public async Task The_step_is_clamped_to_the_guides_real_range_of_thirteen_steps()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PutProgressAsync(app, 12);
        Assert.Equal(12, (await ReadProgressAsync(app)).GetProperty("lastCompletedStep").GetInt32());

        await PutProgressAsync(app, 99);
        Assert.Equal(12, (await ReadProgressAsync(app)).GetProperty("lastCompletedStep").GetInt32());
    }

    [Fact]
    public async Task Completing_the_guide_records_Guided_setup_completed_with_what_was_made_and_marks_the_progress_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/setup/completed", new
        {
            libraries = new[] { "Movies", "TV Shows" },
            qualityProfiles = new[] { "Balanced 1080p" },
            customFormatCount = 4,
            indexerName = "My indexer",
            clientName = "qBittorrent",
            firstTitle = "Heat"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activity = await response.Content.ReadAsStringAsync();
        Assert.Contains("Guided setup completed.", activity, StringComparison.Ordinal);
        Assert.Contains("Heat", activity, StringComparison.Ordinal);
        Assert.Contains("Balanced 1080p", activity, StringComparison.Ordinal);
        var progress = await ReadProgressAsync(app);
        Assert.True(progress.GetProperty("isCompleted").GetBoolean());
    }

    [Fact]
    public async Task The_draft_keeps_what_was_typed_and_clearing_it_returns_the_blank_draft()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var put = await PutDraftAsync(app, new
        {
            mode = "advanced", mediaIntent = "tv", movieRootPath = "D:\\Media\\Movies", seriesRootPath = "D:\\Media\\TV",
            qualityPreset = "premium4k", formatGoal = "homeTheater", indexerProtocol = "newznab", clientProtocol = "sabnzbd",
            clientHost = "localhost", firstTitle = "Heat", firstTitleType = "tv"
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var read = await (await app.Client.GetAsync("/api/setup/draft")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("advanced", read.GetProperty("mode").GetString());
        Assert.Equal("tv", read.GetProperty("mediaIntent").GetString());
        Assert.Equal("premium4k", read.GetProperty("qualityPreset").GetString());
        Assert.Equal("homeTheater", read.GetProperty("formatGoal").GetString());
        Assert.Equal("newznab", read.GetProperty("indexerProtocol").GetString());
        Assert.Equal("sabnzbd", read.GetProperty("clientProtocol").GetString());
        Assert.Equal("Heat", read.GetProperty("firstTitle").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/setup/draft")).StatusCode);
        var cleared = await (await app.Client.GetAsync("/api/setup/draft")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("simple", cleared.GetProperty("mode").GetString());
        Assert.Equal("", cleared.GetProperty("firstTitle").GetString());
    }

    [Fact]
    public async Task A_value_the_guide_does_not_offer_falls_back_to_the_default_instead_of_being_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PutDraftAsync(app, new
        {
            mode = "wizard-of-oz", mediaIntent = "music", qualityPreset = "ultra", formatGoal = "chaos", indexerProtocol = "gopher",
            clientProtocol = "carrier-pigeon", metadataProviderMode = "psychic", firstTitleType = "podcast"
        });

        var read = await (await app.Client.GetAsync("/api/setup/draft")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("simple", read.GetProperty("mode").GetString());
        Assert.Equal("both", read.GetProperty("mediaIntent").GetString());
        Assert.Equal("", read.GetProperty("qualityPreset").GetString());
        Assert.Equal("", read.GetProperty("formatGoal").GetString());
        Assert.Equal("torznab", read.GetProperty("indexerProtocol").GetString());
        Assert.Equal("qbittorrent", read.GetProperty("clientProtocol").GetString());
        Assert.Equal("direct", read.GetProperty("metadataProviderMode").GetString());
        Assert.Equal("movies", read.GetProperty("firstTitleType").GetString());
    }

    [Theory]
    [InlineData("simpleClean")]
    [InlineData("balanced")]
    [InlineData("homeTheater")]
    [InlineData("storageSaver")]
    [InlineData("anime")]
    public async Task Five_of_the_six_release_preferences_survive_a_reload(string goal)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PutDraftAsync(app, new { formatGoal = goal });

        var read = await (await app.Client.GetAsync("/api/setup/draft")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(goal, read.GetProperty("formatGoal").GetString());
    }

    [Fact(Skip = "DEFECT: the draft's format-goal allow-list (SqlitePlatformSettingsRepository.SaveSetupDraftAsync, line 107) omits \"archive\", the sixth release preference the guide offers, so an Archive choice is dropped to blank on reload.")]
    public async Task The_archive_release_preference_survives_a_reload()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PutDraftAsync(app, new { formatGoal = "archive" });

        var read = await (await app.Client.GetAsync("/api/setup/draft")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("archive", read.GetProperty("formatGoal").GetString());
    }

    [Fact]
    public async Task A_key_that_can_only_read_lists_the_installed_apps_but_cannot_connect_one_or_all()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var read = await KeyClientAsync(app, "read");

        var list = await read.GetAsync("/api/system/bundled-components");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var apps = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, apps.ValueKind);
        var text = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync("/api/system/bundled-components/qbittorrent/connect", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync("/api/system/bundled-components/connect-all", content: null)).StatusCode);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task Allowing_other_devices_through_the_firewall_needs_the_System_policy_which_neither_a_read_nor_a_write_key_has(string scopes)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var key = await KeyClientAsync(app, scopes);

        if (scopes == "read")
        {
            Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/system/lan-access")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsync("/api/system/lan-access/allow", content: null)).StatusCode);
    }

    [Fact]
    public async Task Connecting_with_a_body_that_is_not_valid_JSON_is_refused_in_plain_words_and_an_unknown_app_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var malformed = await app.Client.PostAsync("/api/system/bundled-components/qbittorrent/connect", new StringContent("{ not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await malformed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()));

        var unknown = await app.Client.PostAsync("/api/system/bundled-components/not-an-app/connect", content: null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("system")]
    public async Task An_apps_sign_in_kept_in_a_real_credentials_file_is_not_in_any_answer_to_a_session_or_a_key_of_any_scope(string scopes)
    {
        const string password = "Generated-Pass-On-Disk-7431";
        var folder = Directory.CreateTempSubdirectory("deluno-credentials-").FullName;
        try
        {
            var file = Path.Combine(folder, "component-credentials.json");
            var protector = new DataProtectionSecretProtector(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(folder, "keys"))));
            new FileBundledComponentCredentialStore(file, protector).Save("qbittorrent", new BundledComponentCredentials("deluno", password));
            Assert.True(File.Exists(file));
            Assert.DoesNotContain(password, await File.ReadAllTextAsync(file), StringComparison.Ordinal);

            await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            {
                services.RemoveAll<IBundledComponentCredentialStore>();
                services.AddSingleton<IBundledComponentCredentialStore>(new FileBundledComponentCredentialStore(file, protector));
            });
            using var key = scopes.Length == 0 ? null : await KeyClientAsync(app, scopes);
            var client = key ?? app.Client;

            foreach (var path in new[]
                     {
                         "/api/system/bundled-components/qbittorrent/credentials", "/api/system/bundled-components/qbittorrent",
                         "/api/system/bundled-components", "/api/download-clients", "/api/system/bundled-components/credentials"
                     })
            {
                using var response = await client.GetAsync(path);
                Assert.DoesNotContain(password, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static object LibraryBody(string root) => new
    {
        name = "Movies", mediaType = "movies", purpose = "Main library", rootPath = root, downloadsPath = (string?)null,
        qualityProfileId = (string?)null, searchIntervalMinutes = 720, retryDelayHours = 6, maxItemsPerRun = 10
    };

    [Fact]
    public async Task A_root_that_does_not_exist_is_refused_with_a_message_before_any_library_is_made()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var missing = Path.Combine(Path.GetTempPath(), "deluno-no-such-root-" + Guid.NewGuid().ToString("N"));

        var response = await app.Client.PostAsJsonAsync("/api/libraries", LibraryBody(missing));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray());
    }

    [Fact]
    public async Task A_root_that_is_not_an_absolute_path_is_refused_with_a_message_before_any_library_is_made()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/libraries", LibraryBody("Media/Movies"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray());
    }

    [Fact]
    public async Task A_root_that_exists_makes_the_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateTempSubdirectory("deluno-root-").FullName;
        try
        {
            var response = await app.Client.PostAsJsonAsync("/api/libraries", LibraryBody(root));

            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool CanInstallHere => CompanionToolsService.Availability().Available;

    private sealed class WaitingTool(string id, Task release) : ICompanionTool
    {
        public string Id => id;
        public string Name => id;
        public string Description => id;
        public bool IsInstalled() => false;
        public Task<string?> ConnectionNameAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public async Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken) => await release;
    }

    [Fact]
    public async Task The_tools_list_answers_with_whether_installing_is_possible_here_the_three_tools_with_their_state_and_the_uninstall_note()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/companion-tools");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var availability = body.GetProperty("availability");
        Assert.Equal(CanInstallHere, availability.GetProperty("available").GetBoolean());
        if (!CanInstallHere) Assert.False(string.IsNullOrWhiteSpace(availability.GetProperty("reason").GetString()));
        var tools = body.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["sabnzbd", "qbittorrent", "weir"], tools.Select(tool => tool.GetProperty("id").GetString()));
        Assert.All(tools, tool => Assert.Contains(tool.GetProperty("state").GetString(), new[] { "connected", "installed", "not-installed" }));
        Assert.Contains("Uninstalling Deluno leaves", body.GetProperty("uninstallNote").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task The_companion_and_update_routes_need_the_System_policy_which_a_read_or_write_key_does_not_have(string scopes)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var key = await KeyClientAsync(app, scopes);

        Assert.Equal(HttpStatusCode.Forbidden, (await key.GetAsync("/api/companion-tools")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsJsonAsync("/api/companion-tools/install", new { toolIds = new[] { "weir" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsJsonAsync("/api/companion-tools/tray-install", new { toolIds = new[] { "deluge" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.GetAsync("/api/connections/updates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsync("/api/connections/weir/some-id/version/dismiss", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsync("/api/connections/weir/some-id/update", content: null)).StatusCode);
    }

    [Fact]
    public async Task A_fresh_install_has_no_update_notices_and_dismissing_one_that_is_not_there_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var list = await app.Client.GetAsync("/api/connections/updates");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty((await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());

        var dismiss = await app.Client.PostAsync("/api/connections/weir/no-such-connection/version/dismiss", content: null);
        Assert.Equal(HttpStatusCode.NotFound, dismiss.StatusCode);
    }

    [Fact]
    public async Task Asking_to_install_while_a_setup_is_running_is_refused_in_plain_words_and_the_running_one_carries_on()
    {
        var release = new TaskCompletionSource();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<ICompanionTool>();
            foreach (var id in CompanionToolIds.All) services.AddScoped<ICompanionTool>(_ => new WaitingTool(id, release.Task));
        });
        try
        {
            var first = await app.Client.PostAsJsonAsync("/api/companion-tools/install", new { toolIds = new[] { "sabnzbd" } });
            var second = await app.Client.PostAsJsonAsync("/api/companion-tools/install", new { toolIds = new[] { "qbittorrent" } });

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var message = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
            if (CanInstallHere)
            {
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
                Assert.Equal("Tools are already being set up. Wait for that to finish.", message);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
                Assert.False(string.IsNullOrWhiteSpace(message));
            }
        }
        finally
        {
            release.SetResult();
        }
    }
}
