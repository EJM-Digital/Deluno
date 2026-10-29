using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1262: the sweep clears what a finished-with elsewhere hand-off left in the output folder in the places Weir puts
/// it: a release folder holding a file that was sent, and a flat file renamed for a name collision. An unrelated file
/// is left alone. The sources are the recorded Weir hand-offs, moved onto temporary folders.
/// </summary>
public sealed class TheProcessorOutputSweepClearsWhatTheLayoutPutsThereTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T03:00:00Z");

    private readonly string _root = Directory.CreateTempSubdirectory("deluno-1262-sweep-").FullName;
    private readonly string _downloads;
    private readonly string _ready;
    private readonly string _recordedWatched;

    public TheProcessorOutputSweepClearsWhatTheLayoutPutsThereTests()
    {
        _downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        _ready = Directory.CreateDirectory(Path.Combine(_root, "ready")).FullName;
        _recordedWatched = (string)RecordedReplies.Weir("library-folders").Single.Reply.Json()["libraries"]![0]!["watched_folder"]!;
    }

    private readonly List<string> _links = [];

    public void Dispose()
    {
        // A link is taken away on its own first, so that clearing the folder never reaches what it points at.
        foreach (var link in _links.Where(Directory.Exists))
        {
            Directory.Delete(link, recursive: false);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_release_folder_and_a_collision_file_of_released_hand_offs_are_cleared_and_an_unrelated_file_is_left()
    {
        var inside = Moved((string)JsonNode.Parse(RecordedReplies.Weir("handoff-source-files-accepted").Single.Request.Body!)!["sourceFiles"]![0]!);
        var flat = Moved((string)JsonNode.Parse(RecordedReplies.Weir("handoff-accepted").Single.Request.Body!)!["sourcePath"]!);

        var releaseFolder = Path.Combine(_ready, Path.GetFileName(Path.GetDirectoryName(inside)!));
        var inFolder = Put(Path.Combine(releaseFolder, Path.GetFileName(inside)));
        var collision = Put(Path.Combine(_ready, Path.GetFileNameWithoutExtension(flat) + " (1)" + Path.GetExtension(flat)));
        var unrelated = Put(Path.Combine(_ready, "Some.Other.Film.mkv"));

        var removed = await Sweep(
            Handoff("handoff-1", inside),
            Handoff("handoff-2", flat));

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(releaseFolder), "the release folder holding the sent file should be cleared");
        Assert.False(File.Exists(inFolder));
        Assert.False(File.Exists(collision), "the file renamed for a name collision should be cleared");
        Assert.True(File.Exists(unrelated), "a file no hand-off claims must be left alone");
    }

    [Fact]
    public async Task A_hand_off_still_with_the_processor_keeps_its_release_folder()
    {
        var inside = Moved((string)JsonNode.Parse(RecordedReplies.Weir("handoff-source-files-accepted").Single.Request.Body!)!["sourceFiles"]![0]!);
        var releaseFolder = Path.Combine(_ready, Path.GetFileName(Path.GetDirectoryName(inside)!));
        Put(Path.Combine(releaseFolder, Path.GetFileName(inside)));

        var removed = await Sweep(Handoff("handoff-1", inside) with { Status = ProcessorHandoffStatuses.Started });

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(releaseFolder));
    }

    private bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            _links.Add(link);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // No privilege for a symbolic link: a junction, which any Windows user may make, is a reparse point too.
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            mklink.WaitForExit(10_000);
            _links.Add(link);
            return mklink.ExitCode == 0 && Directory.Exists(link);
        }
    }

    private string LibraryMedia()
    {
        var media = Directory.CreateDirectory(Path.Combine(_root, "media", "Dune (2021)")).FullName;
        Put(Path.Combine(media, "Dune (2021).mkv"));
        return media;
    }

    [Fact]
    public async Task A_release_folder_that_is_a_link_to_library_media_is_left_alone_by_the_sweep()
    {
        var media = LibraryMedia();
        var inside = Moved((string)JsonNode.Parse(RecordedReplies.Weir("handoff-source-files-accepted").Single.Request.Body!)!["sourceFiles"]![0]!);
        var link = Path.Combine(_ready, Path.GetFileName(Path.GetDirectoryName(inside)!));
        if (!TryLink(link, media))
        {
            return;
        }

        var removed = await Sweep(Handoff("handoff-1", inside));

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(media, "Dune (2021).mkv")), "the library's file must be untouched");
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void A_second_copy_that_is_a_link_to_library_media_or_holds_one_never_deletes_the_media()
    {
        var media = LibraryMedia();
        var library = new LibraryItem(
            "library-1", "Movies", "movies", "main", Path.Combine(_root, "media"), _downloads, null, null, null,
            true, true, "refine-before-import", "Weir", _ready, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, Now, Now);
        var source = Path.Combine(_downloads, "Dune.2021", "Dune.2021.mkv");

        // The output itself is a link to the library's folder.
        var outputLink = Path.Combine(_ready, "Dune.2021");
        if (!TryLink(outputLink, media))
        {
            return;
        }

        Deluno.Platform.Processing.ProcessorOutputPathPolicy.DeleteSecondCopy(outputLink, _ready, source, [library]);
        Assert.True(File.Exists(Path.Combine(media, "Dune (2021).mkv")), "deleting a linked output must not delete the media it points at");

        // A real release folder holding a link to the library's folder, and nothing else.
        var release = Directory.CreateDirectory(Path.Combine(_ready, "Dune.2021.REAL")).FullName;
        Assert.True(TryLink(Path.Combine(release, "extras"), media));
        Put(Path.Combine(release, "Dune.2021.REAL.mkv"));
        Deluno.Platform.Processing.ProcessorOutputPathPolicy.DeleteSecondCopy(Path.Combine(release, "Dune.2021.REAL.mkv"), _ready, Path.Combine(_downloads, "Dune.2021.REAL", "Dune.2021.REAL.mkv"), [library]);
        Assert.True(File.Exists(Path.Combine(media, "Dune (2021).mkv")), "tidying a release folder must not follow a link into the library");
    }

    private string Moved(string recorded)
        => Path.Combine([_downloads, .. recorded[(_recordedWatched.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries)]);

    private static string Put(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    // Released: Deluno imported the original download instead, so a processed copy will never be imported.
    private static ProcessorHandoffItem Handoff(string id, string sourcePath) => new(
        id, "library-1", "movies", "client-1", "queue-" + id, "Release " + id, sourcePath,
        "Weir", ProcessorHandoffStatuses.Released, null, null, null, Now.AddHours(-3), Now.AddHours(-2));

    private async Task<int> Sweep(params ProcessorHandoffItem[] handoffs)
    {
        var library = new LibraryItem(
            "library-1", "Movies", "movies", "main", "/library", _downloads, null, null, null,
            true, true, "refine-before-import", "Weir", _ready, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, Now, Now);

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsAsync("library-1", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(handoffs);
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repo => repo.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<JobQueueItem>());
        jobs.Setup(repo => repo.ListDownloadDispatchesAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<DownloadDispatchItem>());
        var activity = new Mock<IActivityFeedRepository>();
        var health = new Mock<IHealthCheckRepository>();

        return await new ProcessorOutputSweep(
            libraries.Object, processors.Object, jobs.Object,
            new Mock<IMovieCatalogRepository>().Object, new Mock<ISeriesCatalogRepository>().Object,
            activity.Object, health.Object).SweepAsync(CancellationToken.None);
    }
}
