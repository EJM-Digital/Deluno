using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1262, end to end through the watcher's public entry point: Weir answers a hand-off "completed" with no output path
/// (the recorded <c>completed-without-output-path</c>, Weir rc.13's; rc.18 names the path of a resend, which is a different case), the cleaned file sits flat in the output folder, and Deluno queues
/// the import of that very file. The source, the status reply and the processor's folder list are recorded Weir replies.
/// </summary>
public sealed class AFinishedHandOffWithNoOutputPathStillImportsItsFileTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T03:00:00Z");

    private readonly string _root = Directory.CreateTempSubdirectory("deluno-1262-watch-").FullName;
    private readonly string _downloads;
    private readonly string _ready;
    private readonly string _recordedWatched;
    private readonly string _recordedOutput;
    private readonly Mock<IProcessorRepository> _repository = new();
    private readonly Mock<IProcessorConnectionService> _processor = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly Mock<IProcessorOutputImportQueue> _imports = new();
    private readonly List<string> _queued = [];

    public AFinishedHandOffWithNoOutputPathStillImportsItsFileTests()
    {
        _downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        _ready = Directory.CreateDirectory(Path.Combine(_root, "ready")).FullName;
        var folder = RecordedReplies.Weir("library-folders").Single.Reply.Json()["libraries"]![0]!;
        _recordedWatched = (string)folder["watched_folder"]!;
        _recordedOutput = (string)folder["output_folder"]!;

        _repository.Setup(repo => repo.FindProcessorConnectionAsync(It.IsAny<string?>(), "Weir", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorConnectionItem(
                "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
                "X-Webhook-Secret", "secret", true, "healthy", null, null, Now, Now));
        _repository.Setup(repo => repo.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessorHandoffItem?)null);
        _repository.Setup(repo => repo.RecordProcessorObservationAsync(It.IsAny<string>(), It.IsAny<ProcessorHandoffObservation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repository.Setup(repo => repo.ListProcessorPathMappingsAsync("connection-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorPathMappingItem("m1", "connection-1", _downloads, _recordedWatched, true, 0, Now, Now)]);

        _processor.Setup(service => service.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null));
        _processor.Setup(service => service.GetLibraryFoldersAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorLibraryFolder("library-1", _recordedWatched, true, _recordedOutput)]);

        _imports.Setup(queue => queue.WasAlreadyImportedAsync(It.IsAny<ProcessorHandoffItem>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _imports.Setup(queue => queue.QueueAsync(It.IsAny<LibraryItem>(), It.IsAny<ProcessorHandoffItem>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Callback<LibraryItem, ProcessorHandoffItem, string, string, CancellationToken, string?>((_, _, output, _, _, _) => _queued.Add(output))
            .ReturnsAsync(new ProcessorOutputImport("job-1", "dispatch-1", "movie-1"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_resend_whose_finished_reply_names_no_path_queues_the_import_of_the_flat_file()
    {
        var flat = GivenFlatFileAndCompletedReply(finishedAgo: TimeSpan.FromMinutes(1), replacedAnEarlierSend: true);

        await Watcher().WatchAsync([Library()], Now, CancellationToken.None);

        Assert.Equal(flat, Assert.Single(_queued), ignoreCase: true);
    }

    [Fact]
    public async Task A_finished_hand_off_with_no_path_that_replaced_nothing_queues_the_flat_file_once_it_has_waited()
    {
        var flat = GivenFlatFileAndCompletedReply(finishedAgo: ProcessorHandoffWatcher.ResolveAfter + TimeSpan.FromMinutes(1), replacedAnEarlierSend: false);

        await Watcher().WatchAsync([Library()], Now, CancellationToken.None);

        Assert.Equal(flat, Assert.Single(_queued), ignoreCase: true);
    }

    [Fact]
    public async Task Nothing_is_queued_when_the_output_folder_holds_only_another_file()
    {
        GivenFlatFileAndCompletedReply(finishedAgo: TimeSpan.FromMinutes(1), replacedAnEarlierSend: true, filePresent: false);
        var other = Path.Combine(_ready, "Some.Other.Film.mkv");
        File.WriteAllText(other, "content");
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(-5));

        await Watcher().WatchAsync([Library()], Now, CancellationToken.None);

        Assert.Empty(_queued);
    }

    private string GivenFlatFileAndCompletedReply(TimeSpan finishedAgo, bool replacedAnEarlierSend, bool filePresent = true)
    {
        var recordedSource = (string)JsonNode.Parse(RecordedReplies.Weir("handoff-resend-accepted").Single.Request.Body!)!["sourcePath"]!;
        var source = Path.Combine(_downloads, recordedSource[(_recordedWatched.Length + 1)..]);
        var flat = Path.Combine(_ready, Path.GetFileName(source));
        if (filePresent)
        {
            File.WriteAllText(flat, "content");
            File.SetLastWriteTimeUtc(flat, DateTime.UtcNow.AddMinutes(-5));
        }

        var recorded = RecordedReplies.Weir("completed-without-output-path").Single.Reply.Json();
        Assert.Equal("completed", (string)recorded["state"]!);
        Assert.Null((string?)recorded["outputPath"]);

        _repository.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    "handoff-1", "library-1", "movies", "qbittorrent", "hash-1", "Big Buck Bunny (2008) Recorded R",
                    source, "Weir", ProcessorHandoffStatuses.Started, null, null, null,
                    Now.AddHours(-2), Now.AddHours(-1))
            ]);
        _repository.Setup(repo => repo.ReplacedAnEarlierSendAsync("library-1", "handoff-1", It.IsAny<CancellationToken>())).ReturnsAsync(replacedAnEarlierSend);
        _processor.Setup(service => service.GetHandoffStatusAsync(It.IsAny<ProcessorConnectionItem>(), "handoff-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffStatusResult(
                ProcessorAnswer.Answered,
                (string)recorded["state"]!,
                null,
                null,
                Now - finishedAgo,
                null,
                (string?)recorded["message"]));
        return flat;
    }

    private ProcessorHandoffWatcher Watcher()
        => new(_repository.Object, _processor.Object, _activity.Object, _movies.Object, _series.Object, _imports.Object);

    private LibraryItem Library()
    {
        // The library names no downloads folder: the watched folder can only come from the processor's own list.
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        return library with
        {
            Id = "library-1",
            Name = "Movies",
            MediaType = "movies",
            ImportWorkflow = "refine-before-import",
            ProcessorName = "Weir",
            ProcessorOutputPath = _ready,
            ProcessorTimeoutMinutes = 360,
            ProcessorFailureMode = "block"
        };
    }
}
