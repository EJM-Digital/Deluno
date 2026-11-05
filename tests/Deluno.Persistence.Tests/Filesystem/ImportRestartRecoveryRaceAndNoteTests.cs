using System.Text.Json;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1498. The moments around a restart where the queue's own import and recovery meet: the one that takes the claim
/// first is the import and the other leaves the note for the next pass, a note that cannot be read is never taken for a
/// whole import, and an import that fails is said so and is never counted as finished.
/// </summary>
public sealed class ImportRestartRecoveryRaceAndNoteTests
{
    private static ImportExecuteRequest SanctuaryRequest(ImportRestartRecoveryKeepsMediaTests.Scene scene)
        => new(
            new ImportPreviewRequest(scene.SourcePath, null, "movies", "Sanctuary", 2024, [], [], null, null, ImdbId: "tt0000001"),
            TransferMode: "copy",
            Overwrite: false,
            AllowCopyFallback: true,
            DispatchId: "dispatch-9");

    private static string NoteOf(ImportRestartRecoveryKeepsMediaTests.Scene scene, string? requestJson)
        => JsonSerializer.Serialize(new ImportRestartMarker(scene.SourcePath, scene.DestinationPath, scene.StagingPath, "movies", "Sanctuary", requestJson));

    [Fact]
    public async Task Recovery_that_loses_the_claim_to_the_queue_by_a_hair_puts_its_note_back_for_the_next_pass()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);
        File.WriteAllText(scene.MarkerPath, NoteOf(scene, JsonSerializer.Serialize(SanctuaryRequest(scene))));
        IDisposable? held = null;
        rig.Pipeline.Reply = _ =>
        {
            // The queue's own import takes the file between recovery's look and its call.
            held = ImportSourceClaims.TryClaim(scene.SourcePath);
            return new ImportPipelineResult(false, ImportFileReadiness.RetryableStatusCode, null, "This file is already being imported.");
        };

        try
        {
            var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

            Assert.Equal(new ImportRestartRecoveryReport(0, 0, 1), report);
            Assert.True(File.Exists(scene.MarkerPath), "the note is back for the next pass");
            Assert.Equal("Sanctuary", JsonSerializer.Deserialize<ImportRestartMarker>(File.ReadAllText(scene.MarkerPath))!.Title);
            Assert.True(File.Exists(scene.SourcePath), "the download is untouched");
        }
        finally
        {
            held?.Dispose();
        }

        rig.Pipeline.Reply = null;
        var next = await rig.CreateService().RecoverAsync(CancellationToken.None);
        Assert.Equal(1, next.Recovered);
        Assert.False(File.Exists(scene.MarkerPath));
    }

    [Fact]
    public async Task A_file_placed_before_the_restart_that_loses_the_claim_to_the_queue_keeps_its_note_and_its_copy()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null, stagingPresent: false);
        File.WriteAllBytes(scene.DestinationPath, scene.Bytes);
        File.WriteAllText(scene.MarkerPath, NoteOf(scene, JsonSerializer.Serialize(SanctuaryRequest(scene))));
        IDisposable? held = null;
        rig.Pipeline.Reply = _ =>
        {
            held = ImportSourceClaims.TryClaim(scene.SourcePath);
            return new ImportPipelineResult(false, ImportFileReadiness.RetryableStatusCode, null, "This file is already being imported.");
        };

        try
        {
            var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

            Assert.Equal(new ImportRestartRecoveryReport(0, 0, 1), report);
            Assert.True(File.Exists(scene.MarkerPath));
            Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.DestinationPath));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task A_file_placed_before_the_restart_whose_title_cannot_be_told_now_is_said_in_Activity_and_never_counted_as_finished()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null, stagingPresent: false);
        File.WriteAllBytes(scene.DestinationPath, scene.Bytes);
        File.WriteAllText(scene.MarkerPath, NoteOf(scene, JsonSerializer.Serialize(SanctuaryRequest(scene))));
        rig.Pipeline.Reply = _ => new ImportPipelineResult(false, 409, null, "The title is not in the library any more.");

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(new ImportRestartRecoveryReport(0, 1, 0), report);
        Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.DestinationPath));
        Assert.True(File.Exists(scene.SourcePath));
        var said = await rig.Jobs.ListActivityAsync(50, null, null, CancellationToken.None);
        var line = Assert.Single(said, activity => activity.Category == "filesystem.import.restart-abandoned");
        Assert.Contains("The title is not in the library any more.", line.Message);
        Assert.Contains("Nothing was blocklisted", line.Message);
        Assert.DoesNotContain(said, activity => activity.Category == "filesystem.import.restart-recovered");
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    public async Task A_note_whose_import_cannot_be_read_runs_the_file_alone_and_never_invents_a_title(string requestJson)
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);
        File.WriteAllText(scene.MarkerPath, NoteOf(scene, requestJson));

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        var rerun = Assert.Single(rig.Pipeline.Imports);
        Assert.Equal(scene.SourcePath, rerun.Preview.SourcePath);
        Assert.Equal("movies", rerun.Preview.MediaType);
        Assert.Null(rerun.Preview.Title);
        Assert.Null(rerun.Preview.Year);
        Assert.Equal("auto", rerun.TransferMode);
    }

    [Fact]
    public async Task A_note_an_older_version_wrote_has_only_the_file_and_runs_it_with_no_title()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);
        File.WriteAllText(scene.MarkerPath, NoteOf(scene, null));

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.Null(Assert.Single(rig.Pipeline.Imports).Preview.Title);
    }

    [Fact]
    public async Task A_file_placed_before_the_restart_with_the_download_gone_is_left_in_the_library_and_the_pipeline_is_not_run()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null, stagingPresent: false);
        File.WriteAllBytes(scene.DestinationPath, scene.Bytes);
        File.WriteAllText(scene.MarkerPath, NoteOf(scene, JsonSerializer.Serialize(SanctuaryRequest(scene))));

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.Empty(rig.Pipeline.Imports);
        Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.DestinationPath));
        Assert.False(File.Exists(scene.MarkerPath));
    }
}
