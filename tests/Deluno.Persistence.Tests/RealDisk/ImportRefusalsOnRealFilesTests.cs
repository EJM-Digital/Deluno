using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// What an import refuses, and how a refusal is put right, over real files in a real library: a file that is not a video, a
/// source that is not there, a source still being written, a folder scan that meets a file it cannot read, and a recovery case
/// removed for good. Only the media probe is scripted (the bytes written are not real video).
/// </summary>
public sealed class ImportRefusalsOnRealFilesTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync(Func<string, Deluno.Filesystem.MediaProbeInfo>? probe = null)
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(probe ?? (_ => ScriptedProbe.Good()))));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    [Theory]
    [InlineData("Sintel.2010.WEB.1080p.zip")]
    [InlineData("Sintel.2010.WEB.1080p.srt")]
    [InlineData("Sintel.2010.WEB.1080p.nfo")]
    [InlineData("Sintel.2010.WEB.1080p.iso")]
    public async Task A_file_that_is_not_an_importable_video_is_refused_400_with_the_accepted_types_and_nothing_moves(string fileName)
    {
        await using var world = await StartWorldAsync();
        var source = world.WriteDownload(fileName, fill: 0x33);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "move"));

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("is not configured as an importable video file", result.Message);
        Assert.True(File.Exists(source), "a refused file stays where it was");
        Assert.All(File.ReadAllBytes(source), value => Assert.Equal(0x33, value));
        Assert.Empty(RealDiskLibrary.FilesUnder(world.Root));
        var opened = Assert.Single((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Contains("MKV", opened.RecommendedAction);
    }

    [Fact]
    public async Task A_source_that_is_not_there_is_refused_404_naming_the_mappings_and_account_to_check()
    {
        await using var world = await StartWorldAsync();
        var missing = Path.Combine(world.Downloads, "Sintel.2010.WEB.1080p.mkv");

        var result = await world.ImportAsync(RealDiskLibrary.Request(missing, "Sintel", 2010));

        Assert.False(result.Succeeded);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("The source file was not found at", result.Message);
        Assert.Contains("Deluno checked from its own process", result.Message);
        Assert.Empty(RealDiskLibrary.FilesUnder(world.Root));
        var opened = Assert.Single((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Contains("Docker volume mappings", opened.RecommendedAction);
        Assert.Contains("permissions of", opened.RecommendedAction);
    }

    [Fact]
    public async Task A_source_written_a_moment_ago_is_not_queued_yet_the_answer_says_it_will_wait_and_no_job_exists()
    {
        await using var world = await StartWorldAsync();
        var fresh = Path.Combine(world.Downloads, "Sintel.2010.WEB.1080p.mkv");
        File.WriteAllBytes(fresh, Enumerable.Repeat((byte)0x11, 4096).ToArray());

        var response = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/jobs", new
        {
            preview = new { sourcePath = fresh, mediaType = "movies", title = "Sintel", year = 2010, genres = Array.Empty<string>(), tags = Array.Empty<string>() },
            transferMode = "copy", overwrite = false, allowCopyFallback = true
        });

        Assert.Equal(425, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("still being written", body);
        Assert.Contains("Deluno will wait until it is stable", body);
        Assert.DoesNotContain(await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), job => job.JobType == "filesystem.import.execute");
        Assert.True(File.Exists(fresh));
    }

    private static long Rows(RealDiskLibrary world, string database, string table, string id)
        => Convert.ToInt64(world.ScalarAsync(database, $"SELECT COUNT(*) FROM {table} WHERE id = '{id}';").GetAwaiter().GetResult());

    [Fact]
    public async Task Dismissing_an_import_recovery_case_removes_its_row_for_good_for_a_film_and_for_a_show_and_a_second_dismissal_is_404()
    {
        await using var world = await StartWorldAsync();
        var movieCase = await world.Movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Some Film (2019)", "unpack-failed", "The archive was password protected", "Grab a different release"),
            CancellationToken.None);
        var showCase = await world.App.Services.GetRequiredService<ISeriesCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest("Some Show S01E01", "no-matching-episode", "Nothing matched the file", "Check the numbering"),
            CancellationToken.None);
        Assert.Equal(1, Rows(world, "movies", "movie_import_recovery_cases", movieCase.Id));
        Assert.Equal(1, Rows(world, "series", "series_import_recovery_cases", showCase.Id));

        var movieDismissed = await world.App.Client.DeleteAsync($"/api/movies/import-recovery/{movieCase.Id}");
        var showDismissed = await world.App.Client.DeleteAsync($"/api/series/import-recovery/{showCase.Id}");

        Assert.Equal(HttpStatusCode.NoContent, movieDismissed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, showDismissed.StatusCode);
        Assert.Equal(0, Rows(world, "movies", "movie_import_recovery_cases", movieCase.Id));
        Assert.Equal(0, Rows(world, "series", "series_import_recovery_cases", showCase.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await world.App.Client.DeleteAsync($"/api/movies/import-recovery/{movieCase.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.App.Client.DeleteAsync($"/api/series/import-recovery/{showCase.Id}")).StatusCode);
    }
}
