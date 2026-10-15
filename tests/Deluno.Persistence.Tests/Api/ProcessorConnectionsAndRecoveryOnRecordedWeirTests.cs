using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Tests.Recorded;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What the processor-connection and hand-off recovery routes do beyond the happy path (platform-install rows for connections, Keep
/// waiting, Stop waiting, Import original, Search again and the bell push), against the real replies of Weir 1.0.0-rc.13
/// (tests/fixtures/recorded/weir) and the real reports Weir sent Deluno (tests/fixtures/recorded/deluno), on the whole application with
/// real SQLite files.
/// </summary>
public sealed class ProcessorConnectionsAndRecoveryOnRecordedWeirTests
{
    private const string Secret = "a-webhook-secret";

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static Task<RecordedAppServer> WeirAsync(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept, params string[] states)
        => RecordedAppServer.StartAsync("weir", states.Select(RecordedReplies.Weir), intercept);

    private static object ConnectionForm(RecordedAppServer weir, string? healthCheckUrl = null, string? secret = Secret)
        => new { provider = "weir", submissionUrl = weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret, isEnabled = true, healthCheckUrl };

    private static async Task<JsonElement> ConnectAsync(ApplicationTestHost app, RecordedAppServer weir, string? healthCheckUrl = null)
    {
        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", ConnectionForm(weir, healthCheckUrl));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow = "refine-before-import",
            processorName = "Weir",
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
        return new Scene(library!.Id, downloads, output);
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, string release, string? connectionName = "Weir")
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", "qbittorrent", $"queue-{release}", release, Path.Combine(scene.Downloads, release), connectionName),
            CancellationToken.None);

    /// <summary>Posts a real Weir report of the recorded state: Deluno's ids for the library and the hand-off, everything else as Weir sent it.</summary>
    private static async Task ReportAsync(ApplicationTestHost app, Scene scene, ProcessorHandoffItem handoff, string state)
    {
        var body = JsonNode.Parse(RecordedReplies.Deluno(state).Exchanges[0].Request.Body!)!.AsObject();
        body["libraryId"] = scene.LibraryId;
        body["handoffId"] = handoff.Id;
        var response = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string ReleaseOf(string state)
        => JsonNode.Parse(RecordedReplies.Deluno(state).Exchanges[0].Request.Body!)!["releaseName"]!.GetValue<string>();

    private static async Task<string?> TextAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? null : value.ToString();
    }

    private static async Task<string[]> ActivityCategoriesAsync(ApplicationTestHost app)
    {
        using var body = JsonDocument.Parse(await app.Client.GetStringAsync("/api/activity?pageSize=100"));
        return body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("category").GetString()!).ToArray();
    }

    [Fact]
    public async Task The_connection_list_never_returns_the_secret_the_database_holds_it_encrypted_and_a_blank_secret_on_edit_keeps_it_and_its_health()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await ConnectAsync(app, weir, healthCheckUrl: weir.BaseUrl + "/api/v1/intake/capabilities");
        var id = created.GetProperty("id").GetString()!;

        var listed = await app.Client.GetStringAsync("/api/integrations/processors/connections");
        Assert.DoesNotContain(Secret, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, created.ToString(), StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(listed).RootElement.EnumerateArray().Single().GetProperty("secretConfigured").GetBoolean());

        var stored = await TextAsync(app, "SELECT secret_value FROM processor_connections;");
        Assert.False(string.IsNullOrWhiteSpace(stored));
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);

        // A Test records health; saving again with the secret left blank keeps the secret and the health, and shows neither the secret nor a reset.
        var tested = await app.Client.PostAsync($"/api/integrations/processors/connections/{id}/test", content: null);
        Assert.Equal("healthy", (await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var edit = await app.Client.PutAsJsonAsync($"/api/integrations/processors/connections/{id}", new { provider = "weir", submissionUrl = weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = "", isEnabled = true, healthCheckUrl = weir.BaseUrl + "/api/v1/intake/capabilities" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var after = JsonDocument.Parse(await app.Client.GetStringAsync("/api/integrations/processors/connections")).RootElement.EnumerateArray().Single();
        Assert.True(after.GetProperty("secretConfigured").GetBoolean());
        Assert.Equal("healthy", after.GetProperty("healthStatus").GetString());
        Assert.Equal(Secret, (await Repository(app).GetProcessorConnectionAsync(id, CancellationToken.None))!.Secret);
        Assert.DoesNotContain(Secret, after.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_with_an_address_that_is_not_http_a_header_name_with_a_space_or_a_bad_health_address_is_refused_in_plain_words_and_nothing_is_saved()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var good = weir.BaseUrl + "/api/v1/intake/webhook/deluno";

        var badAddress = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new { provider = "weir", submissionUrl = "ftp://weir.example/hook", secret = Secret, isEnabled = true });
        var badHeader = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new { provider = "weir", submissionUrl = good, authHeaderName = "X Webhook Secret", secret = Secret, isEnabled = true });
        var badHealth = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new { provider = "weir", submissionUrl = good, secret = Secret, isEnabled = true, healthCheckUrl = "weir.example/health" });

        Assert.Equal(HttpStatusCode.BadRequest, badAddress.StatusCode);
        Assert.Contains("Use a valid http or https processor webhook URL.", await badAddress.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, badHeader.StatusCode);
        Assert.Contains("Authentication header names cannot contain spaces.", await badHeader.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, badHealth.StatusCode);
        Assert.Contains("Use a valid http or https address for the health check, or leave it empty.", await badHealth.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/integrations/processors/connections")).Replace(" ", ""));

        // Saving an existing connection is held to the same rules.
        var created = await ConnectAsync(app, weir);
        var id = created.GetProperty("id").GetString()!;
        var edit = await app.Client.PutAsJsonAsync($"/api/integrations/processors/connections/{id}", new { provider = "weir", submissionUrl = "file:///etc/hosts", secret = "", isEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        Assert.Equal(good, JsonDocument.Parse(await app.Client.GetStringAsync("/api/integrations/processors/connections")).RootElement.EnumerateArray().Single().GetProperty("submissionUrl").GetString());
    }

    [Fact]
    public async Task Testing_a_connection_asks_the_real_processor_records_its_health_and_says_so_in_Activity_and_a_failing_test_tells_the_notifications_and_a_recovery_does_too()
    {
        var wrongSecret = RecordedReplies.Weir("capabilities-wrong-secret").Exchanges[0];
        var mode = "up";
        var outbound = new RecordingOutbound();
        await using var weir = await WeirAsync((request, _) => mode == "wrong-secret" && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/capabilities", StringComparison.Ordinal) ? wrongSecret.Reply.ToResponse() : null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        var created = await ConnectAsync(app, weir, healthCheckUrl: weir.BaseUrl + "/api/v1/intake/capabilities");
        var id = created.GetProperty("id").GetString()!;
        var test = $"/api/integrations/processors/connections/{id}/test";

        var healthy = await (await app.Client.PostAsync(test, content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", healthy.GetProperty("status").GetString());
        Assert.Contains(weir.Requests, request => request.Method == "GET" && request.PathAndQuery == "/api/v1/intake/capabilities");
        Assert.Contains("processor.connection.tested", await ActivityCategoriesAsync(app));
        Assert.Equal("healthy", JsonDocument.Parse(await app.Client.GetStringAsync("/api/integrations/processors/connections")).RootElement.EnumerateArray().Single().GetProperty("healthStatus").GetString());
        Assert.DoesNotContain("health.degraded", outbound.Categories);

        // The processor is there but refuses the credential: degraded, said plainly, and the notification goes out.
        mode = "wrong-secret";
        var degraded = await (await app.Client.PostAsync(test, content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", degraded.GetProperty("status").GetString());
        Assert.Contains("rejected the configured credential", degraded.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("degraded", JsonDocument.Parse(await app.Client.GetStringAsync("/api/integrations/processors/connections")).RootElement.EnumerateArray().Single().GetProperty("healthStatus").GetString());
        Assert.Contains("health.degraded", outbound.Categories);

        mode = "up";
        var restoredBefore = outbound.Categories.Count(category => category == "health.restored");
        var recovered = await (await app.Client.PostAsync(test, content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", recovered.GetProperty("status").GetString());
        Assert.Equal(restoredBefore + 1, outbound.Categories.Count(category => category == "health.restored"));
    }

    [Fact]
    public async Task Testing_a_connection_whose_processor_is_switched_off_marks_it_unreachable_and_says_what_to_check()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        string deadAddress;
        await using (var weir = await WeirAsync(null, "capabilities"))
        {
            deadAddress = weir.BaseUrl;
        }

        var created = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new { provider = "weir", submissionUrl = deadAddress + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = Secret, isEnabled = true });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var result = await (await app.Client.PostAsync($"/api/integrations/processors/connections/{id}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("unreachable", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("isReachable").GetBoolean());
        Assert.Contains("Check its URL, network path, and service status.", result.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("unreachable", JsonDocument.Parse(await app.Client.GetStringAsync("/api/integrations/processors/connections")).RootElement.EnumerateArray().Single().GetProperty("healthStatus").GetString());
        Assert.Contains("processor.connection.tested", await ActivityCategoriesAsync(app));
    }

    [Fact]
    public async Task Keeping_on_waiting_for_a_hand_off_Weir_really_failed_watches_it_again_from_now_without_telling_Weir_anything_and_says_so_in_Activity()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ConnectAsync(app, weir);
        var scene = await SceneAsync(app);
        var release = ReleaseOf("events-failed-under-minimum");
        var handoff = await HandoffAsync(app, scene, release, connection.GetProperty("name").GetString());
        await ReportAsync(app, scene, handoff, "events-failed-under-minimum");
        var failed = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Failed, failed!.Status);
        var told = weir.Requests.Count;

        var keep = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/keep-waiting", content: null);

        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        var resumed = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Started, resumed!.Status);
        Assert.True(resumed.UpdatedUtc >= failed.UpdatedUtc, "the limit counts from this change, so it starts again");
        Assert.Null(resumed.FailureMessage);
        Assert.Equal(told, weir.Requests.Count);
        Assert.Contains("processing.kept-waiting", await ActivityCategoriesAsync(app));
        Assert.Contains(release, (await keep.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_the_wait_on_a_hand_off_that_has_already_ended_says_there_is_nothing_left_to_wait_for_and_changes_nothing()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ConnectAsync(app, weir);
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, ReleaseOf("events-failed-under-minimum"), connection.GetProperty("name").GetString());
        await ReportAsync(app, scene, handoff, "events-failed-under-minimum");
        var before = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        var told = weir.Requests.Count;

        var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/stop-waiting", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("nothing left to wait for", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        var after = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(before!.Status, after!.Status);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(told, weir.Requests.Count);
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    [InlineData(ProcessorHandoffStatuses.Accepted)]
    [InlineData(ProcessorHandoffStatuses.Started)]
    [InlineData(ProcessorHandoffStatuses.Completed)]
    public async Task The_original_is_not_imported_while_the_processor_may_still_deliver_and_nothing_changes(string status)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, "Still.With.Weir.2020.1080p.WEB-DL-DELUNO");
        if (status != ProcessorHandoffStatuses.Waiting)
        {
            await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, status, null, null, null, CancellationToken.None);
        }

        var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("will not import a file the processor may still deliver", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(status, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.DoesNotContain("processing.released", await ActivityCategoriesAsync(app));
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    [InlineData(ProcessorHandoffStatuses.TimedOut)]
    [InlineData(ProcessorHandoffStatuses.Stalled)]
    [InlineData(ProcessorHandoffStatuses.Lost)]
    [InlineData(ProcessorHandoffStatuses.Cancelled)]
    public async Task The_original_is_imported_instead_once_the_processor_failed_went_quiet_stalled_lost_or_was_told_to_stop_with_a_line_in_Activity(string ended)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, "Ended.Badly.2020.1080p.WEB-DL-DELUNO");
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ended, null, null, "The processor did not finish.", CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Released, stored!.Status);
        Assert.Contains("unprocessed", stored.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("processing.released", await ActivityCategoriesAsync(app));
    }

    [Fact]
    public async Task Giving_up_on_a_hand_off_Weir_really_failed_blocklists_the_release_by_its_download_id_and_its_name_and_ends_the_hand_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var release = ReleaseOf("events-failed-unreadable");
        var handoff = await HandoffAsync(app, scene, release);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(scene.LibraryId, "movies", "movie", "movie-1", release, "Fixture", "qbittorrent", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: $"queue-{release}");
        await ReportAsync(app, scene, handoff, "events-failed-unreadable");

        var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", content: null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var blocked = JsonDocument.Parse(await app.Client.GetStringAsync("/api/blocked-releases")).RootElement.EnumerateArray().Single();
        Assert.Equal(release, blocked.GetProperty("releaseName").GetString());
        Assert.Equal($"queue-{release}", blocked.GetProperty("torrentHashOrItemId").GetString());
        Assert.Equal("Fixture", blocked.GetProperty("indexerName").GetString());
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Contains("processing.search-again", await ActivityCategoriesAsync(app));
    }

    [Fact]
    public async Task Giving_up_on_a_hand_off_nobody_knows_the_title_of_is_refused_and_a_hand_off_that_has_not_ended_badly_is_refused_and_nothing_is_blocklisted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var noTitle = await HandoffAsync(app, scene, "Nobody.Knows.2020.1080p.WEB-DL-DELUNO");
        await Repository(app).UpdateProcessorHandoffAsync(noTitle.Id, ProcessorHandoffStatuses.Failed, null, null, "It failed.", CancellationToken.None);
        var working = await HandoffAsync(app, scene, "Still.Working.2020.1080p.WEB-DL-DELUNO");

        var unknownTitle = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{noTitle.Id}/search-again", content: null);
        var notEnded = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{working.Id}/search-again", content: null);

        Assert.Equal(HttpStatusCode.Conflict, unknownTitle.StatusCode);
        Assert.Contains("does not know which title this release was for", (await unknownTitle.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, notEnded.StatusCode);
        Assert.Contains("only be started once this hand-off has ended badly", (await notEnded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await Repository(app).GetProcessorHandoffAsync(noTitle.Id, CancellationToken.None))!.Status);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/blocked-releases")).Replace(" ", ""));
    }

    [Fact]
    public async Task Every_successful_write_to_a_hand_off_route_pushes_attention_changed_to_the_open_bell_and_a_refused_one_pushes_nothing()
    {
        await using var host = await SharedPipelineHost.StartAsync(root: Path.Combine(Path.GetTempPath(), "deluno-attention-push", Guid.NewGuid().ToString("N")));
        var created = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("owner", "Owner", "Deluno-Test-2026!"));
        var token = (await created.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var processors = host.Services.GetRequiredService<IProcessorRepository>();
        var jobs = host.Services.GetRequiredService<IJobQueueRepository>();

        async Task<ProcessorHandoffItem> FailedAsync(string release)
        {
            var handoff = await processors.EnsureProcessorHandoffAsync(
                new CreateProcessorHandoffRequest("lib-films", "movies", "qbittorrent", $"queue-{release}", release, $"/downloads/{release}", "Weir"),
                CancellationToken.None);
            return (await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Failed, null, null, "Weir could not read the file.", CancellationToken.None))!;
        }

        var dismissed = await FailedAsync("Dismiss.Me.2020.1080p.WEB-DL-DELUNO");
        var original = await FailedAsync("Import.Me.2020.1080p.WEB-DL-DELUNO");
        var again = await FailedAsync("Search.Me.2020.1080p.WEB-DL-DELUNO");
        var dispatchId = await jobs.RecordDownloadDispatchAsync("lib-films", "movies", "movie", "movie-1", again.ReleaseName, "Fixture", "qbittorrent", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await host.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: again.QueueItemId);
        var untouched = await FailedAsync("Not.Written.2020.1080p.WEB-DL-DELUNO");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(host.Client.BaseAddress!, "/hubs/deluno"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();
        var received = new ConcurrentQueue<string>();
        connection.On<JsonElement>("RealtimeEvent", envelope => received.Enqueue(envelope.GetProperty("name").GetString()!));
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", new[] { "dashboard", "queue", "activity", "live" });
        await connection.InvokeAsync<JsonElement>("Resume", 0L, new[] { "dashboard", "queue", "activity", "live" }, (string?)null);
        await ((IHostedService)host.Services.GetRequiredService<IRealtimeEventPublisher>()).StartAsync(CancellationToken.None);

        async Task<int> PushesAfterAsync(Func<Task<HttpResponseMessage>> write, bool expectPush)
        {
            var before = received.Count(name => name == LiveEventNames.AttentionChanged);
            var response = await write();
            Assert.Equal(expectPush, response.IsSuccessStatusCode);
            if (expectPush)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && received.Count(name => name == LiveEventNames.AttentionChanged) <= before)
                {
                    await Task.Delay(50); // reason: poll interval inside a bounded wait (10 s) for a push arriving over a real socket
                }
            }

            return received.Count(name => name == LiveEventNames.AttentionChanged) - before;
        }

        Assert.True(await PushesAfterAsync(() => host.Client.PostAsync($"/api/integrations/processors/handoffs/{dismissed.Id}/dismiss", null), expectPush: true) >= 1, "dismiss pushes");
        Assert.True(await PushesAfterAsync(() => host.Client.PostAsync($"/api/integrations/processors/handoffs/{original.Id}/import-original", null), expectPush: true) >= 1, "import original pushes");
        Assert.True(await PushesAfterAsync(() => host.Client.PostAsync($"/api/integrations/processors/handoffs/{again.Id}/search-again", null), expectPush: true) >= 1, "search again pushes");

        // Let the pushes the three writes caused arrive before looking for one that must not.
        var settled = -1;
        while (settled != received.Count(name => name == LiveEventNames.AttentionChanged))
        {
            settled = received.Count(name => name == LiveEventNames.AttentionChanged);
            await Task.Delay(TimeSpan.FromSeconds(1.5)); // reason: waits out the production coalesce window (a real timer) until no more pushes arrive
        }

        // A write that fails changes nothing and so tells the bell nothing.
        var quiet = settled;
        var refused = await host.Client.PostAsync($"/api/integrations/processors/handoffs/{dismissed.Id}/import-original", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // reason: a push that must NOT happen can only be shown absent once the production coalesce window (a real timer) has passed
        Assert.Equal(quiet, received.Count(name => name == LiveEventNames.AttentionChanged));
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await processors.GetProcessorHandoffAsync(untouched.Id, CancellationToken.None))!.Status);
        Assert.True(File.Exists(Path.Combine(host.DataRoot, "platform.db")), "the hand-offs are in the platform database file on disk");
    }

    private sealed class RecordingOutbound : IOutboundNotificationService
    {
        public List<string> Categories { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            Categories.Add(eventCategory);
            return Task.CompletedTask;
        }

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
