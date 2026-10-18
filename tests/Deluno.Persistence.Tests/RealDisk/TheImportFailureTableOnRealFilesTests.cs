using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The failure table walked through the real import pipeline over real files and a real database: a file Deluno has read and
/// knows is wrong (a sample, no video stream, a probe that rejects it) refuses the release at once and starts a replacement
/// search; a file Deluno could not read is tried once more before the release is refused; a missing source says nothing about
/// the release and refuses nothing. Each leaves the download where it was, and the recovery case says in words what it was.
/// Only the media probe is scripted (the bytes written are not real video).
/// </summary>
public sealed class TheImportFailureTableOnRealFilesTests
{
    private const string Release = "Sintel.2010.WEB.1080p-DELUNO";

    private static async Task<RealDiskLibrary> StartAsync(Func<string, MediaProbeInfo> probe)
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(probe)));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static async Task<IReadOnlyList<BlockedRelease>> BlockedAsync(RealDiskLibrary world)
        => await world.App.Services.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None);

    private static async Task<bool> SearchQueuedAsync(RealDiskLibrary world)
        => (await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None))
            .Any(job => job.JobType.StartsWith("movies.search", StringComparison.OrdinalIgnoreCase) || job.JobType.Contains("search", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task A_sample_is_refused_at_once_its_release_is_blocked_a_replacement_is_searched_for_and_the_download_stays()
    {
        await using var world = await StartAsync(_ => ScriptedProbe.Good(seconds: 90));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x44);
        var dispatch = await world.RecordDispatchAsync(movie.Id, Release);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: dispatch));

        Assert.False(result.Succeeded);
        var blocked = Assert.Single(await BlockedAsync(world));
        Assert.Equal(ImportFailurePolicy.LikelySample, blocked.ReasonCode);
        Assert.Equal(BlockedReleaseStates.Refused, blocked.State);
        Assert.Equal(Release, blocked.ReleaseName);
        Assert.True(await SearchQueuedAsync(world), "a refused release starts a replacement search");
        Assert.True(File.Exists(source), "refusing the release does not delete the download; the sharing rule decides that later");
        var opened = Assert.Single((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Contains("likely sample", opened.Summary);
        // The case keeps one kind for every refusal of a file; its words, and the blocklist entry above, carry which reason it was.
        Assert.Equal(ImportFailurePolicy.ImportFailed, opened.FailureKind);
    }

    [Fact]
    public async Task A_file_with_no_video_stream_is_refused_at_once_and_the_case_says_so()
    {
        await using var world = await StartAsync(_ => new MediaProbeInfo("succeeded", "test", null, 7200, "matroska", 1_000_000, [], [], []));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x45);
        var dispatch = await world.RecordDispatchAsync(movie.Id, Release);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: dispatch));

        Assert.False(result.Succeeded);
        Assert.Equal(ImportFailurePolicy.NoVideoStream, Assert.Single(await BlockedAsync(world)).ReasonCode);
        var opened = Assert.Single((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Contains("No video stream was detected", opened.Summary);
        Assert.Contains("Choose a valid video file", opened.RecommendedAction);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_file_the_playability_check_rejects_is_refused_at_once_with_the_checks_own_words()
    {
        await using var world = await StartAsync(_ => ScriptedProbe.Failed("The file ends in the middle of a frame."));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x46);
        var dispatch = await world.RecordDispatchAsync(movie.Id, Release);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: dispatch));

        Assert.False(result.Succeeded);
        Assert.Equal(ImportFailurePolicy.MediaProbeRejected, Assert.Single(await BlockedAsync(world)).ReasonCode);
        var opened = Assert.Single((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Contains("The file ends in the middle of a frame.", opened.Summary);
        Assert.Contains("complete, playable", opened.RecommendedAction);
    }

    [Fact]
    public async Task A_file_that_could_not_be_read_is_not_refused_the_first_time_and_is_refused_when_the_same_release_fails_again()
    {
        await using var world = await StartAsync(_ => new MediaProbeInfo("unreadable", "test", "The share dropped while the file was being read.", null, null, null, [], [], []));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x47);
        var first = await world.RecordDispatchAsync(movie.Id, Release);

        Assert.False((await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: first))).Succeeded);

        Assert.Empty(await BlockedAsync(world));
        var opened = (await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.First();
        Assert.Contains("The share dropped", opened.Summary);
        Assert.Contains("The release itself is not suspected", opened.RecommendedAction);

        // The same release is sent again and fails the same way: now it is refused.
        var second = await world.RecordDispatchAsync(movie.Id, Release);
        Assert.False((await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: second))).Succeeded);

        var blocked = Assert.Single(await BlockedAsync(world));
        Assert.Equal(ImportFailurePolicy.MediaProbeUnreadable, blocked.ReasonCode);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_source_that_is_not_there_says_nothing_about_the_release_so_nothing_is_refused_and_no_search_starts()
    {
        await using var world = await StartAsync(_ => ScriptedProbe.Good());
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var missing = Path.Combine(world.Downloads, Release + ".mkv");
        var dispatch = await world.RecordDispatchAsync(movie.Id, Release);

        var result = await world.ImportAsync(RealDiskLibrary.Request(missing, "Sintel", 2010, dispatchId: dispatch));

        Assert.Equal(404, result.StatusCode);
        Assert.Empty(await BlockedAsync(world));
        Assert.False(await SearchQueuedAsync(world));
        var opened = Assert.Single((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Contains("The source file was not found", opened.Summary);
    }

    [Fact]
    public void Every_reason_the_pipeline_can_give_has_an_answer_for_whether_it_deletes_counts_against_the_client_or_stops_the_library()
    {
        // The table itself, read as the pipeline reads it: only a wrong file is deleted, only a vanished source is a client
        // strike, and only a setup problem that would fail every title stops the library searching.
        static string[] Sorted(IEnumerable<string> reasons) => reasons.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            Sorted([ImportFailurePolicy.NoVideoStream, ImportFailurePolicy.LikelySample, ImportFailurePolicy.MediaProbeRejected, ImportFailurePolicy.ReplacementRejected, ImportFailurePolicy.DownloadIntegrityFailed]),
            Sorted(ImportFailurePolicy.KnownReasons.Where(ImportFailurePolicy.ShouldDeletePayload)));
        Assert.Equal([ImportFailurePolicy.MissingSource], ImportFailurePolicy.KnownReasons.Where(ImportFailurePolicy.CountsAsClientStrike).ToArray());
        Assert.Equal(
            Sorted([ImportFailurePolicy.MissingLibraryRoot, ImportFailurePolicy.Permission, ImportFailurePolicy.HardlinkUnavailable, ImportFailurePolicy.HardlinkFailed]),
            Sorted(ImportFailurePolicy.KnownReasons.Where(ImportFailurePolicy.StopsSearching)));
        Assert.All(ImportFailurePolicy.KnownReasons, reason =>
        {
            var decision = ImportFailurePolicy.BlockFor(reason);
            Assert.True(
                decision is BlockDecision.Immediately or BlockDecision.AfterOneRetry or BlockDecision.Never,
                $"{reason} has no answer");
            Assert.False(string.IsNullOrWhiteSpace(ImportFailurePolicy.CategoryFor(reason)), $"{reason} is filed under no heading");
            // A reason the release is refused for is never filed as the person's own setup.
            if (decision is BlockDecision.Immediately)
            {
                Assert.NotEqual(FailureCategories.YourSetup, ImportFailurePolicy.CategoryFor(reason));
            }
        });
    }
}
