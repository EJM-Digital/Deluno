using System.Net;
using System.Net.Http.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1242, from the rc.30.20 golden run (Retthree, Twicefilm, Twicetwo): a person said "I do not want it" and sent the
/// release again. The processor had already started the first send, and reported it finished a few seconds after the
/// resend was asked for but before Deluno had made the resend's own hand-off. Weir logged
/// <c>POST /api/integrations/processors/events returned HTTP 409</c> at 22:18:20Z, 23:11:54Z and 23:15:59Z.
///
/// <para>The reports are the ones the real Weir sent (tests/fixtures/recorded/deluno/&lt;version&gt;/events-*, #1239), with
/// only the library, the hand-off and the folder changed to the test's own: handoffId, status, processorName, libraryId,
/// releaseName, outputPath, message and outputFiles - and no source path. The one report no recording holds, a rejection, is
/// built here and listed in tests/fixtures/recorded/MISSING.md.</para>
///
/// <para>#1239: a report Weir really sent (tests/fixtures/recorded/deluno/*/events-*) is posted from its recording wherever one matches the case; a case with no real report stays
/// inline on purpose, and tests/fixtures/recorded/MISSING.md lists it with the Weir state that would produce it.</para>
/// </summary>
public sealed class ALateReportForARemovedSendIsSettledTests
{
    private const string Release = "Retthree.2020.1080p.WEB-DL.x264-GOLDEN";

    private sealed record Scene(string LibraryId, string Output, string DownloadFolder, string Film);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Ready")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(downloads, Release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, Release + ".mkv"), new byte[64]);
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
        var created = await Repository(app).CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "generic-webhook", "http://127.0.0.1:9/hook", null, null, true),
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "processor_connections", created.Id, "Weir");
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Retthree", releaseYear = 2020, monitored = true });
        added.EnsureSuccessStatusCode();
        var film = (await added.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
        return new Scene(library!.Id, output, folder, film);
    }

    private static Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, string clientId, string queueItemId, string? handoffReleaseName = null)
        => Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", clientId, queueItemId, handoffReleaseName ?? Release, scene.DownloadFolder, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);

    private static Task RecordSendAsync(ApplicationTestHost app, Scene scene, string clientId, string queueItemId)
        => app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", scene.Film, Release, "Fixture Indexer",
            clientId, "Client", "sent", null, clientExternalId: queueItemId, cancellationToken: CancellationToken.None);

    private static async Task<string> WriteOutputAsync(Scene scene)
    {
        var file = Path.Combine(scene.Output, Release + ".mkv");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        return file;
    }

    /// <summary>Send, the processor starts it, "I do not want it" (the hand-off ends), then the release is sent again - but the new hand-off is not made yet.</summary>
    private static async Task<ProcessorHandoffItem> RemovedAndAskedForAgainAsync(ApplicationTestHost app, Scene scene, string clientId, string queueItemId, string endedAs, string? handoffReleaseName = null)
    {
        await RecordSendAsync(app, scene, clientId, queueItemId);
        var first = await HandoffAsync(app, scene, clientId, queueItemId, handoffReleaseName);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, endedAs, null, null, "You said you do not want it.", CancellationToken.None);
        await Wait.ClockMovesOnAsync();
        await RecordSendAsync(app, scene, clientId, queueItemId);
        return first;
    }

    /// <summary>Weir's recorded report of a finished single file (events-completed, the file flat in its output folder), for this test's release.</summary>
    private static Task<HttpResponseMessage> WeirReportsAsync(ApplicationTestHost app, Scene scene, string handoffId, string outputPath)
    {
        var body = RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed"), scene.LibraryId, handoffId, scene.Output, Release + ".mkv");
        Assert.Equal([outputPath], RecordedWeirReports.OutputFilesOf(body));
        return RecordedWeirReports.PostAsync(app.Client, body);
    }

    /// <summary>Weir's recorded report of a failure, for this test's release.</summary>
    private static Task<HttpResponseMessage> WeirFailsAsync(ApplicationTestHost app, Scene scene, string handoffId, string state)
        => RecordedWeirReports.PostAsync(
            app.Client,
            RecordedWeirReports.Body(RecordedWeirReports.Exchange(state), scene.LibraryId, handoffId, scene.Output, Release + ".mkv"));

    public static IEnumerable<object[]> Clients()
    {
        yield return ["transmission-1", "hash-retthree", ProcessorHandoffStatuses.Dismissed];
        yield return ["qbittorrent-1", "hash-retthree", ProcessorHandoffStatuses.Dismissed];
        yield return ["sabnzbd-1", "SABnzbd_nzo_retthree", ProcessorHandoffStatuses.Dismissed];
        yield return ["nzbget-1", "42", ProcessorHandoffStatuses.Dismissed];
        yield return ["transmission-1", "hash-retthree", ProcessorHandoffStatuses.Cancelled];
        yield return ["transmission-1", "hash-retthree", ProcessorHandoffStatuses.Released];
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task The_first_sends_finished_report_before_the_resends_hand_off_exists_is_settled_and_never_a_409(string clientId, string queueItemId, string endedAs)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var first = await RemovedAndAskedForAgainAsync(app, scene, clientId, queueItemId, endedAs);
        var output = await WriteOutputAsync(scene);

        var response = await WeirReportsAsync(app, scene, first.Id, output);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Contains("alreadyHandled", await response.Content.ReadAsStringAsync());
        // Nothing is imported from a report about a send that was removed: the resend settles from its own.
        Assert.DoesNotContain(
            await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None),
            job => job.JobType == "filesystem.import.execute");
        Assert.True(File.Exists(output), "the processor's file is left where it is for the resend to find");
    }

    /// <summary>
    /// The rc.30.29 golden run (resend.during.transmission 17:44:34Z, resend.during.deluge 17:47:11Z): the hand-off of a
    /// single-file torrent carries the client's own name for it, "...-GOLDEN.mkv", while the send Deluno recorded is named
    /// "...-GOLDEN". Usenet hand-offs carry the same name on both sides, so only the torrent clients answered 409.
    /// </summary>
    [Theory]
    [InlineData("transmission-1", "hash-retthree")]
    [InlineData("deluge-1", "hash-retthree")]
    [InlineData("qbittorrent-1", "hash-retthree")]
    public async Task The_first_sends_late_report_is_settled_when_the_client_names_the_file_with_its_extension(string clientId, string queueItemId)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var first = await RemovedAndAskedForAgainAsync(app, scene, clientId, queueItemId, ProcessorHandoffStatuses.Dismissed, Release + ".mkv");
        var output = await WriteOutputAsync(scene);

        var response = await WeirReportsAsync(app, scene, first.Id, output);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Contains("alreadyHandled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_report_for_a_removed_send_that_nobody_asked_for_again_is_still_too_late()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        await RecordSendAsync(app, scene, "transmission-1", "hash-retthree");
        var first = await HandoffAsync(app, scene, "transmission-1", "hash-retthree");
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Dismissed, null, null, "You said you do not want it.", CancellationToken.None);
        var output = await WriteOutputAsync(scene);

        var response = await WeirReportsAsync(app, scene, first.Id, output);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("too late", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("held")]
    [InlineData("unreadable")]
    [InlineData("gone")]
    public async Task A_failed_report_for_a_removed_send_that_was_asked_for_again_is_settled_too(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var first = await RemovedAndAskedForAgainAsync(app, scene, "transmission-1", "hash-retthree", ProcessorHandoffStatuses.Dismissed);

        // A hold for a file under Weir's minimum size, a file it cannot read, and a download that was gone are Weir's recorded reports.
        // A rejection has never been recorded (the golden VM has no workflow that rejects), so it stays as Weir's code builds it.
        var response = kind switch
        {
            "rejected" => await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new { handoffId = first.Id, status = "failed", processorName = "Weir", libraryId = scene.LibraryId, releaseName = Release + ".mkv", message = "bad release", disposition = "rejected", sourceRemoved = true, outputFiles = Array.Empty<string>() }),
            "held" => await WeirFailsAsync(app, scene, first.Id, "events-failed-under-minimum"),
            "unreadable" => await WeirFailsAsync(app, scene, first.Id, "events-failed-unreadable"),
            _ => await WeirFailsAsync(app, scene, first.Id, "events-failed-source-gone")
        };

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(first.Id, CancellationToken.None))?.Status);
    }

    /// <summary>Next door: the same report once the resend's hand-off exists (the "before" and "after" timings), torrent names and Usenet names alike.</summary>
    [Theory]
    [InlineData("transmission-1", "hash-retthree", Release + ".mkv")]
    [InlineData("deluge-1", "hash-retthree", Release + ".mkv")]
    [InlineData("sabnzbd-1", "SABnzbd_nzo_retthree", Release)]
    [InlineData("nzbget-1", "42", Release)]
    public async Task The_first_sends_report_after_the_resends_hand_off_exists_is_settled_and_never_a_409(string clientId, string queueItemId, string handoffReleaseName)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var first = await RemovedAndAskedForAgainAsync(app, scene, clientId, queueItemId, ProcessorHandoffStatuses.Dismissed, handoffReleaseName);
        var second = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", clientId, queueItemId, handoffReleaseName, scene.DownloadFolder, "Weir", DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        var output = await WriteOutputAsync(scene);

        var response = await WeirReportsAsync(app, scene, first.Id, output);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_report_for_a_removed_torrent_send_named_with_its_extension_that_nobody_asked_for_again_is_still_too_late()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        await RecordSendAsync(app, scene, "transmission-1", "hash-retthree");
        var first = await HandoffAsync(app, scene, "transmission-1", "hash-retthree", Release + ".mkv");
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Dismissed, null, null, "You said you do not want it.", CancellationToken.None);
        var output = await WriteOutputAsync(scene);

        var response = await WeirReportsAsync(app, scene, first.Id, output);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed class Captured : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Sink(categoryName, Lines);
        public void Dispose() { }

        private sealed class Sink(string category, List<string> lines) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == ProcessorEventAnswerLog.Category && logLevel == Microsoft.Extensions.Logging.LogLevel.Information)
                {
                    lock (lines) lines.Add(formatter(state, exception));
                }
            }
        }
    }

    /// <summary>#1242: Weir logged "returned HTTP 409" and Deluno's log said nothing. Every answer is now one Information line.</summary>
    [Fact]
    public async Task Every_answer_to_a_processor_report_is_written_to_the_log_with_its_status_hand_off_and_reason()
    {
        var captured = new Captured();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddLogging(logging => logging.AddProvider(captured)));
        var scene = await SceneAsync(app);
        var first = await RemovedAndAskedForAgainAsync(app, scene, "transmission-1", "hash-retthree", ProcessorHandoffStatuses.Dismissed, Release + ".mkv");
        var output = await WriteOutputAsync(scene);
        Assert.Equal(HttpStatusCode.Accepted, (await WeirReportsAsync(app, scene, first.Id, output)).StatusCode);

        // A hand-off nobody sent again: the genuine "too late".
        var lone = await RemovedAndAskedForAgainAsync(app, scene, "qbittorrent-1", "hash-lone", ProcessorHandoffStatuses.Dismissed);
        await Repository(app).UpdateProcessorHandoffAsync(lone.Id, ProcessorHandoffStatuses.Dismissed, null, null, "x", CancellationToken.None);
        var unknown = await WeirReportsAsync(app, scene, "no-such-handoff", output);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);

        string[] lines;
        lock (captured.Lines) lines = [.. captured.Lines];
        Assert.Contains(lines, line => line.Contains("processors/events answered 202 (already handled)") && line.Contains(first.Id));
        Assert.Contains(lines, line => line.Contains("processors/events answered 409") && line.Contains("no-such-handoff") && line.Contains("could not match"));
    }
}
