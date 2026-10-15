using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// ProcessorHandoffWatcher against the real replies of Weir 1.0.0-rc.13 (tests/fixtures/recorded/weir) served over a real loopback
/// port, on a real library folder and real database files: a hand-off Weir is queued on or working is waited for and the answer
/// kept; a hand-off Weir says it never received is sent again once and then given up with the person asked; a hand-off Weir
/// finished whose report never came has its processed file imported; a Weir that cannot be asked is not given up on.
/// </summary>
public sealed class ProcessorHandoffWatcherAgainstRecordedWeirTests
{
    private const string Secret = "a-webhook-secret";

    private sealed record Scene(string LibraryId, string Downloads, string Output, string ConnectionName);

    private static Task<RecordedAppServer> WeirAsync(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept, params string[] states)
        => RecordedAppServer.StartAsync("weir", states.Select(RecordedReplies.Weir), intercept);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, RecordedAppServer weir)
    {
        var connected = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new
        {
            provider = "weir",
            submissionUrl = weir.BaseUrl + "/api/v1/intake/webhook/deluno",
            authHeaderName = "X-Webhook-Secret",
            secret = Secret,
            isEnabled = true
        });
        Assert.True(connected.IsSuccessStatusCode, await connected.Content.ReadAsStringAsync());
        var name = (await connected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads,
            importWorkflow = "refine-before-import", processorName = name, processorOutputPath = output
        });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        return new Scene((await library.Content.ReadFromJsonAsync<LibraryItem>())!.Id, downloads, output, name);
    }

    private static Func<HttpRequestMessage, string, HttpResponseMessage?> Status(string state) => RecordedAppServer.Answering("GET", "/handoffs/deluno/", RecordedReplies.Weir(state).Exchanges[0]);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    /// <summary>A hand-off sent to Weir and waiting on it, with the download's files on disk where the library says they are.</summary>
    private static async Task<ProcessorHandoffItem> SentAsync(ApplicationTestHost app, Scene scene, string release)
    {
        var source = Path.Combine(scene.Downloads, release);
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, release + ".mkv"), [1, 2, 3, 4]);
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", "qbittorrent", $"queue-{release}", release, source, scene.ConnectionName),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
    }

    private static async Task WatchAsync(ApplicationTestHost app, DateTimeOffset now)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var watcher = new ProcessorHandoffWatcher(
            services.GetRequiredService<IProcessorRepository>(),
            services.GetRequiredService<IProcessorConnectionService>(),
            services.GetRequiredService<Deluno.Jobs.Data.IActivityFeedRepository>(),
            services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>(),
            services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>(),
            services.GetService<IProcessorOutputImportQueue>(),
            services.GetService<Deluno.Jobs.Data.IDownloadDispatchesRepository>());
        var libraries = await services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        await watcher.WatchAsync(libraries, now, CancellationToken.None, libraries);
    }

    private static string Seen(RecordedAppServer weir, ProcessorHandoffItem? handoff = null) => "seen: " + string.Join(" ; ", weir.Requests.Select(r => r.Method + " " + r.PathAndQuery)) + " | unanswered: " + string.Join(" ; ", weir.Unanswered) + " | processor: " + System.Text.Json.JsonSerializer.Serialize(handoff?.Processor) + " | message: " + handoff?.FailureMessage + " | status: " + handoff?.Status;

    private static int StatusPolls(RecordedAppServer weir) => weir.Requests.Count(request => request.Method == "GET" && request.PathAndQuery.Contains("/handoffs/deluno/", StringComparison.Ordinal));

    [Fact]
    public async Task A_hand_off_Weir_is_working_on_is_waited_for_and_Weirs_own_words_are_kept()
    {
        await using var weir = await WeirAsync(Status("status-working"), "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, weir);
        var handoff = await SentAsync(app, scene, "Working.Film.2020.1080p.WEB-DL-DELUNO");

        await WatchAsync(app, DateTimeOffset.UtcNow);

        Assert.True(StatusPolls(weir) >= 1, "the watcher really asks Weir what it is doing");
        var after = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Submitted, after.Status);
        Assert.True(after.Processor?.State == "working", Seen(weir, after));
        Assert.Equal("Weir has claimed this file and is checking it now.", after.Processor.Message);
    }

    [Fact]
    public async Task A_hand_off_queued_behind_others_is_waited_for_however_long_it_has_been_there_and_its_place_in_line_is_kept()
    {
        await using var weir = await WeirAsync(Status("status-queued"), "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, weir);
        var handoff = await SentAsync(app, scene, "Queued.Film.2020.1080p.WEB-DL-DELUNO");

        await WatchAsync(app, DateTimeOffset.UtcNow.AddHours(5));

        var after = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Submitted, after.Status);
        Assert.True(after.Processor?.State == "queued", Seen(weir, after));
        Assert.Equal(12, after.Processor.QueuePosition);
    }

    [Fact]
    public async Task A_hand_off_Weir_never_received_is_left_alone_inside_the_grace_then_sent_again_once_and_then_given_up_with_the_person_asked()
    {
        await using var weir = await WeirAsync(RecordedAppServer.Chain(Status("status-unknown-id"), RecordedAppServer.Answering("POST", "/webhook/deluno", RecordedReplies.Weir("handoff-resend-accepted").Exchanges[0])), "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, weir);
        var handoff = await SentAsync(app, scene, "Lost.Film.2020.1080p.WEB-DL-DELUNO");
        var sentBefore = weir.Requests.Count(request => request.Method == "POST" && request.PathAndQuery.EndsWith("/webhook/deluno", StringComparison.Ordinal));

        await WatchAsync(app, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(ProcessorHandoffStatuses.Submitted, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(sentBefore, weir.Requests.Count(request => request.Method == "POST" && request.PathAndQuery.EndsWith("/webhook/deluno", StringComparison.Ordinal)));

        await WatchAsync(app, DateTimeOffset.UtcNow.AddMinutes(20));
        var resent = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Submitted, resent.Status);
        Assert.True(resent.FailureMessage?.StartsWith(ProcessorHandoffWatcher.SentAgainNotice, StringComparison.Ordinal) == true, Seen(weir, resent));
        Assert.Equal(sentBefore + 1, weir.Requests.Count(request => request.Method == "POST" && request.PathAndQuery.EndsWith("/webhook/deluno", StringComparison.Ordinal)));
        Assert.Contains("processing.resubmitted", await app.Client.GetStringAsync("/api/activity?take=50"), StringComparison.Ordinal);

        await WatchAsync(app, DateTimeOffset.UtcNow.AddMinutes(40));
        var lost = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Lost, lost.Status);
        Assert.Contains("Send it again once", lost.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(sentBefore + 1, weir.Requests.Count(request => request.Method == "POST" && request.PathAndQuery.EndsWith("/webhook/deluno", StringComparison.Ordinal)));
        Assert.True(File.Exists(Path.Combine(scene.Downloads, "Lost.Film.2020.1080p.WEB-DL-DELUNO", "Lost.Film.2020.1080p.WEB-DL-DELUNO.mkv")), "giving up never deletes the download");
    }

    [Fact]
    public async Task A_hand_off_Weir_finished_whose_report_never_came_has_its_processed_file_imported_and_left_in_place()
    {
        const string release = "Finished.Film.2020.1080p.WEB-DL-DELUNO";
        await using var weir = await WeirAsync(Status("status-completed"), "capabilities", "library-folders");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, weir);
        var handoff = await SentAsync(app, scene, release);
        var processed = Path.Combine(scene.Output, release, release + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(processed)!);
        await File.WriteAllBytesAsync(processed, Enumerable.Repeat((byte)9, 2048).ToArray());
        File.SetLastWriteTimeUtc(processed, DateTime.UtcNow.AddMinutes(-30));

        await WatchAsync(app, DateTimeOffset.UtcNow.AddHours(2));

        var after = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.True(after.Status == ProcessorHandoffStatuses.Completed, Seen(weir, after));
        Assert.Equal(processed, after.OutputPath);
        Assert.True(File.Exists(processed), "the processed file is still where the processor put it until the import moves it");
        Assert.All(await File.ReadAllBytesAsync(processed), value => Assert.Equal(9, value));
        Assert.Contains("processing.completed.import-queued", await app.Client.GetStringAsync("/api/activity?take=50"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Weir_that_cannot_be_asked_is_not_given_up_on_and_after_an_hour_of_silence_the_person_is_told()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, weir);
        var handoff = await SentAsync(app, scene, "Silent.Film.2020.1080p.WEB-DL-DELUNO");
        var start = DateTimeOffset.UtcNow;
        await weir.DisposeAsync();

        await WatchAsync(app, start);
        await WatchAsync(app, start.AddMinutes(61));

        var after = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Submitted, after.Status);
        Assert.NotNull(after.Processor!.UnreachableSinceUtc);
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("processing.processor-unreachable", activity, StringComparison.Ordinal);
        Assert.Contains("will not give up on them while it cannot ask", activity, StringComparison.Ordinal);
    }
}
