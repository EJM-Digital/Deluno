using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What Transfers is told about a finished download in a library that refines before importing: a real qBittorrent 5.2.1 reply
/// (tests/fixtures/recorded, a finished torrent changed only in where it says its files are: a real folder on this disk) and the
/// hand-off record Deluno keeps, read through <c>GET /api/download-clients/telemetry</c>. While the processor has it the download
/// is waiting for the processor and is counted apart from the imports that are ready; once the processor has given up it reads
/// processing failed with the processor's own reason; and a download whose import ran and failed reads import failed.
/// </summary>
public sealed class TheTransfersReadingNamesWhereAFinishedDownloadIsTests
{
    private sealed record Scene(ApplicationTestHost App, RecordedAppServer Qbit, string LibraryId, string Hash, string Release, string Source) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Qbit.DisposeAsync();
        }
    }

    private static async Task<Scene> SceneAsync(string? handoffStatus, string? failure = null, string workflow = "refine-before-import", bool importJob = false)
    {
        var app = await ApplicationTestHost.StartAsync();
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films", mediaType = "movies", rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName,
            downloadsPath = downloads, importWorkflow = workflow, processorName = workflow == "copy" ? null : "Weir", processorOutputPath = workflow == "copy" ? null : output
        });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<LibraryItem>())!.Id;

        // A finished torrent, as qBittorrent really described one, with its files where this test put them.
        var listing = JsonNode.Parse(RecordedReplies.Qbittorrent("finished-folder").Body("torrents/info"))!.AsArray();
        var torrent = listing[0]!.AsObject();
        var hash = torrent["hash"]!.GetValue<string>();
        var release = torrent["name"]!.GetValue<string>();
        var source = Directory.CreateDirectory(Path.Combine(downloads, release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, release + ".mkv"), new byte[4096]);
        torrent["content_path"] = source;
        torrent["save_path"] = downloads;
        var body = listing.ToJsonString();
        var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "trackers" }.Select(RecordedReplies.Qbittorrent),
            (request, _) => request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) ? RecordedReplies.Json(body) : null);
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var client = await created.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = client.GetProperty("id").GetString()!;

        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-1", release, "Fixture", clientId, "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: hash);
        await dispatches.RecordDetectionAsync(dispatchId, hash, null, completed: true, CancellationToken.None);

        if (handoffStatus is not null)
        {
            var processors = app.Services.GetRequiredService<IProcessorRepository>();
            var handoff = await processors.EnsureProcessorHandoffAsync(new CreateProcessorHandoffRequest(libraryId, "movies", clientId, hash, release, source, "Weir"), CancellationToken.None);
            string? importJobId = null;
            if (importJob)
            {
                importJobId = (await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(new EnqueueJobRequest("filesystem.import.execute", "test", "{}", "movie", "movie-1", IdempotencyKey: "import-" + hash), CancellationToken.None)).Id;
            }

            await processors.UpdateProcessorHandoffAsync(handoff.Id, handoffStatus, importJob ? Path.Combine(output, release + ".mkv") : null, importJobId, failure, CancellationToken.None);
            if (importJobId is not null)
            {
                // Then the import ran, and gave up, after the processor handed its output back.
                await Task.Delay(50); // reason: the job's give-up must be stamped after the hand-off's last update, which is what the row compares
                var db = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
                await using var connection = await db.OpenConnectionAsync("jobs", CancellationToken.None);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE job_queue SET status = 'dead-letter', last_error = 'The destination file already exists.', last_attempt_utc = @now WHERE id = @id;";
                var id = command.CreateParameter();
                id.ParameterName = "@id";
                id.Value = importJobId;
                command.Parameters.Add(id);
                var now = command.CreateParameter();
                now.ParameterName = "@now";
                now.Value = DateTimeOffset.UtcNow.ToString("O");
                command.Parameters.Add(now);
                await command.ExecuteNonQueryAsync();
            }
        }

        return new Scene(app, qbit, libraryId, hash, release, source);
    }

    private static async Task<(JsonElement Item, JsonElement Summary)> ReadAsync(Scene scene)
    {
        var overview = await scene.App.Client.GetFromJsonAsync<JsonElement>("/api/download-clients/telemetry");
        var queue = overview.GetProperty("clients")[0].GetProperty("queue").EnumerateArray().ToArray();
        return (Assert.Single(queue), overview.GetProperty("summary").Clone());
    }

    [Fact]
    public async Task A_finished_download_with_no_hand_off_yet_is_waiting_for_the_processor_and_counted_apart_from_the_imports_that_are_ready()
    {
        await using var scene = await SceneAsync(handoffStatus: null);

        var (item, summary) = await ReadAsync(scene);

        Assert.Equal("waitingForProcessor", item.GetProperty("status").GetString());
        Assert.Equal(1, summary.GetProperty("waitingForProcessorCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("importReadyCount").GetInt32());
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    [InlineData(ProcessorHandoffStatuses.Accepted)]
    [InlineData(ProcessorHandoffStatuses.Started)]
    public async Task A_download_the_processor_still_has_is_waiting_for_the_processor_whatever_stage_the_hand_off_is_at(string status)
    {
        await using var scene = await SceneAsync(status);

        var (item, summary) = await ReadAsync(scene);

        Assert.Equal("waitingForProcessor", item.GetProperty("status").GetString());
        Assert.Equal(1, summary.GetProperty("waitingForProcessorCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("importReadyCount").GetInt32());
        Assert.Equal(scene.Hash, item.GetProperty("id").GetString(), ignoreCase: true);
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    [InlineData(ProcessorHandoffStatuses.TimedOut)]
    [InlineData(ProcessorHandoffStatuses.Stalled)]
    [InlineData(ProcessorHandoffStatuses.Lost)]
    public async Task A_hand_off_the_processor_gave_up_on_reads_processing_failed_with_the_processors_own_reason_and_is_not_blamed_on_the_client(string status)
    {
        await using var scene = await SceneAsync(status, failure: "The processor refused the hand-off: its watched folder is not set.");

        var (item, summary) = await ReadAsync(scene);

        Assert.Equal("processingFailed", item.GetProperty("status").GetString());
        Assert.Equal("The processor refused the hand-off: its watched folder is not set.", item.GetProperty("errorMessage").GetString());
        var failure = item.GetProperty("failure");
        Assert.Equal("processor", failure.GetProperty("serviceType").GetString());
        Assert.Equal(0, summary.GetProperty("waitingForProcessorCount").GetInt32());
    }

    [Fact]
    public async Task A_download_whose_import_ran_and_gave_up_reads_import_failed_and_is_not_waiting_for_anyone()
    {
        await using var scene = await SceneAsync(handoffStatus: ProcessorHandoffStatuses.Submitted, importJob: true);

        var (item, summary) = await ReadAsync(scene);

        Assert.Equal("importFailed", item.GetProperty("status").GetString());
        Assert.Equal(0, summary.GetProperty("waitingForProcessorCount").GetInt32());
    }

    [Fact]
    public async Task A_hand_off_the_person_released_goes_through_the_ordinary_import_so_the_client_says_where_it_is()
    {
        await using var scene = await SceneAsync(ProcessorHandoffStatuses.Released, failure: "Importing the original, unprocessed, as requested.");

        var (item, _) = await ReadAsync(scene);

        Assert.NotEqual("processingFailed", item.GetProperty("status").GetString());
        Assert.NotEqual("waitingForProcessor", item.GetProperty("status").GetString());
    }
}
