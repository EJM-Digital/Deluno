using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the guide's progress and draft routes at their edges (a fresh install, the step a finished guide re-opens on, a draft
/// the reader cannot parse) and the recovery notices' access rules and unknown-kind answers, over HTTP on the real host.
/// </summary>
public sealed class SetupAndRecoveryRoutesLowRowsTests
{
    private sealed class OneNotice : IComponentRecoveryNotices
    {
        public bool Pending { get; private set; } = true;
        public int Readds { get; private set; }

        public Task<IReadOnlyList<ComponentRecoveryNotice>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ComponentRecoveryNotice>>(Pending
                ? [new ComponentRecoveryNotice("transmission", "Transmission", "Transmission's settings were rebuilt; 3 torrents may need adding back.", 3, true)]
                : []);

        public Task<ComponentRecoveryResult?> ReaddAsync(string kind, CancellationToken cancellationToken)
        {
            if (kind != "transmission" || !Pending) return Task.FromResult<ComponentRecoveryResult?>(null);
            Readds++;
            Pending = false;
            return Task.FromResult<ComponentRecoveryResult?>(new ComponentRecoveryResult(true, "Deluno added 3 torrents back to Transmission."));
        }

        public Task<bool> DismissAsync(string kind, CancellationToken cancellationToken)
        {
            if (!Pending || kind != "transmission") return Task.FromResult(false);
            Pending = false;
            return Task.FromResult(true);
        }
    }

    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Key " + scopes, scopes });
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static Task<ApplicationTestHost> StartWithNotice(OneNotice notices)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IComponentRecoveryNotices>();
            services.AddSingleton<IComponentRecoveryNotices>(notices);
        });

    [Fact]
    public async Task A_fresh_install_reads_step_0_not_skipped_not_completed_and_a_saved_progress_reads_back_with_its_time()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var fresh = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress");

        Assert.Equal(0, fresh.GetProperty("lastCompletedStep").GetInt32());
        Assert.False(fresh.GetProperty("isSkipped").GetBoolean());
        Assert.False(fresh.GetProperty("isCompleted").GetBoolean());
        Assert.True(fresh.TryGetProperty("updatedUtc", out _));

        await app.Client.PutAsJsonAsync("/api/setup/progress", new { lastCompletedStep = 7, isSkipped = true, isCompleted = false });
        var saved = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress");

        Assert.Equal(7, saved.GetProperty("lastCompletedStep").GetInt32());
        Assert.True(saved.GetProperty("isSkipped").GetBoolean());
        Assert.False(saved.GetProperty("isCompleted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("updatedUtc").ValueKind);
    }

    [Fact]
    public async Task Completing_the_guide_writes_step_4_so_a_finished_guide_reopens_on_Other_devices_and_not_on_Start()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Client.PutAsJsonAsync("/api/setup/progress", new { lastCompletedStep = 12, isSkipped = false, isCompleted = false });

        var done = await app.Client.PostAsJsonAsync("/api/setup/completed", new { libraries = new[] { "Movies" }, customFormatCount = 0 });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        var progress = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress");
        Assert.Equal(4, progress.GetProperty("lastCompletedStep").GetInt32());
        Assert.True(progress.GetProperty("isCompleted").GetBoolean());
    }

    [Fact]
    public async Task A_stored_draft_that_cannot_be_read_comes_back_as_the_blank_draft_and_not_as_an_error()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Client.PutAsJsonAsync("/api/setup/draft", new { mode = "advanced", firstTitle = "Heat" });
        await ApiScenes.ExecuteAsync(app, "platform", "UPDATE system_settings SET setting_value = '{ this is not json' WHERE setting_key = 'setup.draft.v1';");

        var response = await app.Client.GetAsync("/api/setup/draft");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("simple", draft.GetProperty("mode").GetString());
        Assert.Equal("", draft.GetProperty("firstTitle").GetString());
    }

    [Fact]
    public async Task Adding_back_an_app_that_has_no_notice_is_not_found_and_does_not_touch_the_one_that_has()
    {
        var notices = new OneNotice();
        await using var app = await StartWithNotice(notices);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/system/component-recovery/qbittorrent/readd", null)).StatusCode);

        Assert.Equal(0, notices.Readds);
        Assert.True(notices.Pending);
        Assert.Equal(1, (await app.Client.GetFromJsonAsync<JsonElement>("/api/system/component-recovery")).GetArrayLength());
    }

    [Fact]
    public async Task A_key_that_can_only_read_lists_the_notices_but_cannot_add_back_or_dismiss_and_a_write_key_can_and_a_caller_with_no_key_is_turned_away()
    {
        var notices = new OneNotice();
        await using var app = await StartWithNotice(notices);
        using var read = await KeyClientAsync(app, "read");
        using var write = await KeyClientAsync(app, "write");
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/system/component-recovery")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync("/api/system/component-recovery/transmission/readd", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync("/api/system/component-recovery/transmission/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/system/component-recovery")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/system/component-recovery/transmission/readd", null)).StatusCode);
        Assert.True(notices.Pending);

        Assert.Equal(HttpStatusCode.OK, (await write.PostAsync("/api/system/component-recovery/transmission/readd", null)).StatusCode);
        Assert.Equal(1, notices.Readds);
    }
}
