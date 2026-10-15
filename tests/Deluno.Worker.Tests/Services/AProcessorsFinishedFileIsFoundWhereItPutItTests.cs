using System.Text.Json.Nodes;
using Deluno.Platform.Contracts;
using Deluno.Platform.Processing;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1262: a processor's finished hand-off that names no output path is still found. Weir puts the cleaned file at the
/// source's path relative to the folder it watches, under the workflow's output folder, so a single film at the watched
/// root lands flat and a film in a release folder keeps that folder. Every source, output and folder here is a recorded
/// Weir reply (tests/fixtures/recorded/weir), moved onto this machine's temporary folders; none is written by hand.
/// </summary>
public sealed class AProcessorsFinishedFileIsFoundWhereItPutItTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-1262-").FullName;
    private readonly string _downloads;
    private readonly string _ready;
    private readonly string _recordedWatched;
    private readonly string _recordedOutput;

    public AProcessorsFinishedFileIsFoundWhereItPutItTests()
    {
        _downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        _ready = Directory.CreateDirectory(Path.Combine(_root, "ready")).FullName;
        var movies = LibraryFolders()["libraries"]![0]!;
        _recordedWatched = (string)movies["watched_folder"]!;
        _recordedOutput = (string)movies["output_folder"]!;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_flat_single_file_is_found_at_the_output_folder_when_the_completed_reply_names_no_path()
    {
        var source = SourceOf("handoff-accepted");
        var expected = Put(Rebased(OutputOf("completed-with-output-path"), _recordedOutput, _ready));

        // The reply that sent the file back reported its path; this one (a resend's) did not.
        Assert.Null(NullOutputPathOf("completed-without-output-path"));
        var found = WorkPlanner.FindCorrelatedProcessorOutputs(_ready, Rebased(source, _recordedWatched, _downloads), _downloads);

        Assert.Equal(expected, Assert.Single(found), ignoreCase: true);
    }

    [Fact]
    public void A_flat_single_file_is_found_after_a_resend_whose_finished_reply_has_no_path()
    {
        // rc.13 gave a resend's finished status no path (rc.18 gives one); the shape is kept for an older Weir.
        Assert.Null(NullOutputPathOf("completed-without-output-path"));
        var source = Rebased(SourceOf("handoff-resend-accepted"), _recordedWatched, _downloads);
        var expected = Put(Path.Combine(_ready, Path.GetFileName(source)));

        Assert.Equal(expected, Assert.Single(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source, _downloads)), ignoreCase: true);
    }

    [Fact]
    public void A_folder_hand_off_is_found_in_the_release_folder_the_processor_kept()
    {
        var source = Rebased(SourceOf("handoff-release-folder-accepted"), _recordedWatched, _downloads);
        var expected = Put(Rebased(OutputOf("release-folder-status-completed"), _recordedOutput, _ready));

        Assert.Equal(expected, Assert.Single(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source, _downloads)), ignoreCase: true);
    }

    [Fact]
    public void A_file_inside_a_release_folder_is_found_in_that_folder()
    {
        var file = FirstSourceFile();
        var expected = Put(Rebased(OutputOf("source-files-status-completed"), _recordedOutput, _ready));

        var found = WorkPlanner.FindCorrelatedProcessorOutputs(_ready, Rebased(file, _recordedWatched, _downloads), _downloads);

        Assert.Equal(expected, Assert.Single(found), ignoreCase: true);
    }

    /// <summary>
    /// Weir 1.0.0-rc.18 under its "keep both" policy (recorded: <c>output-collision-keep-both-*</c>): a different file sent under a name the
    /// output already held comes out as <c>stem (2).ext</c> beside the first (not "(1)": Weir numbers from 2). It is found as the same
    /// download's, and with the "replace" policy (<c>output-collision-replace-*</c>) the second lands on the very same path.
    /// </summary>
    [Fact]
    public void A_name_collision_that_renamed_only_the_file_is_found()
    {
        var firstOutput = Put(Rebased(OutputOf("output-collision-keep-both-first-status"), _recordedOutput, _ready));
        var secondOutput = Put(Rebased(OutputOf("output-collision-keep-both-second-status"), _recordedOutput, _ready));
        Assert.Equal(Path.GetFileNameWithoutExtension(firstOutput) + " (2)", Path.GetFileNameWithoutExtension(secondOutput));
        // Both sends were the same name in the watched folder: the plain name, then the same name again.
        var source = Path.Combine(_downloads, Path.GetFileName(firstOutput));

        var found = WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source, _downloads);

        Assert.Contains(secondOutput, found, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(firstOutput, found, StringComparer.OrdinalIgnoreCase);
        // A reported path for the renamed file is about the same download.
        Assert.True(ProcessorOutputPathPolicy.IsOutputAbout(secondOutput, _ready, source));

        var replaced = Rebased(OutputOf("output-collision-replace-second-status"), _recordedOutput, _ready);
        Assert.Equal(replaced, Rebased(OutputOf("output-collision-replace-first-status"), _recordedOutput, _ready), ignoreCase: true);
    }

    /// <summary>Weir rc.18 answers a single file sent again while it held the first send with the output at the flat name, like any other single file.</summary>
    [Fact]
    public void A_flat_single_file_taken_over_from_the_first_send_is_found_at_the_output_folder()
    {
        var output = Put(Rebased(OutputOf("resend-takeover-second-final-status"), _recordedOutput, _ready));
        var source = Path.Combine(_downloads, Path.GetFileName(output));

        Assert.Equal(output, Assert.Single(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source, _downloads)), ignoreCase: true);
    }

    [Fact]
    public void Another_file_beside_it_is_never_taken_for_it()
    {
        var source = Rebased(SourceOf("handoff-accepted"), _recordedWatched, _downloads);
        Put(Path.Combine(_ready, "Some.Other.Film.mkv"));
        Put(Path.Combine(_ready, Path.GetFileNameWithoutExtension(source) + " Extended.mkv"));

        Assert.Empty(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source, _downloads));
    }

    [Fact]
    public void Nothing_outside_the_processors_own_output_folder_is_ever_matched()
    {
        var source = Rebased(SourceOf("handoff-accepted"), _recordedWatched, _downloads);
        // The same name in the downloads folder and one level above the output folder, never inside it.
        Put(Path.Combine(_downloads, Path.GetFileName(source)));
        Put(Path.Combine(_root, Path.GetFileName(source)));
        // A source that climbs out of the watched folder reads no relative path.
        var escaping = _downloads + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + Path.GetFileName(source);

        Assert.Empty(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source, _downloads));
        Assert.Empty(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, escaping, _downloads));
    }

    [Fact]
    public void The_stem_named_folder_is_still_found_when_the_layout_finds_nothing()
    {
        // The documented generic layout (a drop-folder processor names a folder after the source minus its extension)
        // is kept for a processor that publishes no folder list, and for a library with no downloads folder.
        var source = Rebased(SourceOf("handoff-accepted"), _recordedWatched, _downloads);
        var inFolder = Put(Path.Combine(_ready, Path.GetFileNameWithoutExtension(source), Path.GetFileName(source)));

        Assert.Equal(inFolder, Assert.Single(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source)), ignoreCase: true);
        Assert.Equal(inFolder, Assert.Single(WorkPlanner.FindCorrelatedProcessorOutputs(_ready, source, _downloads)), ignoreCase: true);
    }

    [Fact]
    public void The_watched_folder_comes_from_the_processors_own_list_through_the_path_mapping()
    {
        var entry = LibraryFolders()["libraries"]![0]!;
        var key = (string)entry["manager_library_key"]!;
        var folders = new[] { new ProcessorLibraryFolder(key, _recordedWatched, true, _recordedOutput) };
        var mappings = new[]
        {
            new ProcessorPathMappingItem("m1", "c1", _downloads, _recordedWatched, true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        };

        Assert.Equal(_downloads, ProcessorOutputLayout.WatchedFolderFor(key, "/not/the/same", folders, mappings), ignoreCase: true);
        Assert.Equal("/downloads", ProcessorOutputLayout.WatchedFolderFor("some-other-library", "/downloads", folders, mappings));
    }

    [Fact]
    public void A_top_level_item_belongs_to_the_hand_off_that_the_layout_puts_there()
    {
        var flat = Rebased(SourceOf("handoff-accepted"), _recordedWatched, _downloads);
        var folder = Rebased(SourceOf("handoff-release-folder-accepted"), _recordedWatched, _downloads);
        var inside = Rebased(FirstSourceFile(), _recordedWatched, _downloads);
        var releaseFolderName = Path.GetFileName(Path.GetDirectoryName(inside)!);

        Assert.True(ProcessorOutputLayout.ClaimsTopLevelName(flat, _downloads, Path.GetFileName(flat)));
        Assert.True(ProcessorOutputLayout.ClaimsTopLevelName(flat, _downloads, Path.GetFileNameWithoutExtension(flat) + " (2)" + Path.GetExtension(flat)));
        Assert.True(ProcessorOutputLayout.ClaimsTopLevelName(folder, _downloads, Path.GetFileName(folder)));
        Assert.True(ProcessorOutputLayout.ClaimsTopLevelName(inside, _downloads, releaseFolderName));
        Assert.False(ProcessorOutputLayout.ClaimsTopLevelName(flat, _downloads, "Some.Other.Film.mkv"));
        Assert.False(ProcessorOutputLayout.ClaimsTopLevelName(flat, _downloads, Path.GetFileNameWithoutExtension(flat) + " Extended.mkv"));
        Assert.False(ProcessorOutputLayout.ClaimsTopLevelName(inside, _downloads, "Some.Other.Release"));
    }

    [Fact]
    public void The_names_a_hand_off_may_own_include_the_release_folder_of_a_file_inside_one()
    {
        var inside = Rebased(FirstSourceFile(), _recordedWatched, _downloads);
        var releaseFolderName = Path.GetFileName(Path.GetDirectoryName(inside)!);

        Assert.Contains(releaseFolderName, WorkPlanner.ExpectedOutputDirectoryNames(inside, _downloads), StringComparer.OrdinalIgnoreCase);
    }

    private static JsonNode LibraryFolders() => RecordedReplies.Weir("library-folders").Single.Reply.Json();

    private static string SourceOf(string state) => (string)JsonNode.Parse(RecordedReplies.Weir(state).Single.Request.Body!)!["sourcePath"]!;

    private static string FirstSourceFile() => (string)JsonNode.Parse(RecordedReplies.Weir("handoff-source-files-accepted").Single.Request.Body!)!["sourceFiles"]![0]!;

    private static string OutputOf(string state) => (string)RecordedReplies.Weir(state).Single.Reply.Json()["outputPath"]!;

    private static string? NullOutputPathOf(string state) => (string?)RecordedReplies.Weir(state).Single.Reply.Json()["outputPath"];

    /// <summary>A recorded Weir path moved from the recorded folder onto this machine's folder, keeping what is below it.</summary>
    private static string Rebased(string recorded, string recordedRoot, string localRoot)
    {
        Assert.StartsWith(recordedRoot + "\\", recorded, StringComparison.OrdinalIgnoreCase);
        var below = recorded[(recordedRoot.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([localRoot, .. below]);
    }

    private static string Put(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }
}
