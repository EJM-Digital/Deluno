using System.Net;
using System.Net.Http.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1242, from the rc.30.20 golden run (Retthree, Twicefilm, Twicetwo): a person said "I do not want it" and sent the
/// release again. The processor had already started the first send, and reported it finished a few seconds after the
/// resend was asked for but before Deluno had made the resend's own hand-off. Weir logged
/// <c>POST /api/integrations/processors/events returned HTTP 409</c> at 22:18:20Z, 23:11:54Z and 23:15:59Z.
///
/// <para>The request is the one Weir's <c>CompletionReports.BuildCompletionBody</c> builds: handoffId, status,
/// processorName, libraryId, releaseName, outputPath, message and outputFiles - and no source path.</para>
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

    private static Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, string clientId, string queueItemId)
        => Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", clientId, queueItemId, Release, scene.DownloadFolder, "Weir", DateTimeOffset.UnixEpoch),
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
    private static async Task<ProcessorHandoffItem> RemovedAndAskedForAgainAsync(ApplicationTestHost app, Scene scene, string clientId, string queueItemId, string endedAs)
    {
        await RecordSendAsync(app, scene, clientId, queueItemId);
        var first = await HandoffAsync(app, scene, clientId, queueItemId);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, endedAs, null, null, "You said you do not want it.", CancellationToken.None);
        await Task.Delay(30);
        await RecordSendAsync(app, scene, clientId, queueItemId);
        return first;
    }

    private static Task<HttpResponseMessage> WeirReportsAsync(ApplicationTestHost app, Scene scene, string handoffId, string outputPath)
        => app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            handoffId,
            status = "completed",
            processorName = "Weir",
            libraryId = scene.LibraryId,
            releaseName = Release + ".mkv",
            outputPath,
            message = "Weir processed the file.",
            outputFiles = new[] { outputPath }
        });

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
    [InlineData("gone")]
    public async Task A_failed_report_for_a_removed_send_that_was_asked_for_again_is_settled_too(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var first = await RemovedAndAskedForAgainAsync(app, scene, "transmission-1", "hash-retthree", ProcessorHandoffStatuses.Dismissed);

        // The failed reports Weir's CompletionReports.BuildCompletionBody builds: a rejection, a hold, a download that was gone.
        object body = kind switch
        {
            "rejected" => new { handoffId = first.Id, status = "failed", processorName = "Weir", libraryId = scene.LibraryId, releaseName = Release + ".mkv", message = "bad release", disposition = "rejected", sourceRemoved = true, outputFiles = Array.Empty<string>() },
            "held" => new { handoffId = first.Id, status = "failed", processorName = "Weir", libraryId = scene.LibraryId, releaseName = Release + ".mkv", message = "could not process", disposition = "held", sourceRemoved = false, outputFiles = Array.Empty<string>() },
            _ => new { handoffId = first.Id, status = "failed", processorName = "Weir", libraryId = scene.LibraryId, releaseName = Release + ".mkv", message = "no longer there", sourceRemoved = false, failureClass = "source-gone", outputFiles = Array.Empty<string>() }
        };
        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", body);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(first.Id, CancellationToken.None))?.Status);
    }
}
