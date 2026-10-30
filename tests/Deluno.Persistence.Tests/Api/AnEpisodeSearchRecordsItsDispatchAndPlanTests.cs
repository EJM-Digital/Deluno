using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// An episode search that finds a candidate (coverage register, engines): the send is recorded as a dispatch started by the person, with the
/// replacement flag true only when the episode already has a file, and the search attempt keeps its plan. Against Jackett 0.24.2850's
/// and qBittorrent 5.2.1's recorded replies. Refs #1296.
/// </summary>
public sealed class AnEpisodeSearchRecordsItsDispatchAndPlanTests
{
    [Fact]
    public async Task A_manual_episode_search_for_a_missing_episode_records_a_dispatch_that_is_not_replacement_authorised_and_the_attempt_with_its_plan()
    {
        await using var rig = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.RigAsync();
        var (seriesId, episodeId) = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.AddShowAsync(rig);

        var answer = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId } });

        Assert.True(answer.GetProperty("matchedCount").GetInt32() >= 1, answer.ToString());
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{episodeId}';"));
        Assert.Equal("manual", await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT started_by FROM download_dispatches WHERE entity_id = '{episodeId}';"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT replacement_authorized FROM download_dispatches WHERE entity_id = '{episodeId}';"));
        var dispatched = await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT release_name FROM download_dispatches WHERE entity_id = '{episodeId}';");
        Assert.False(string.IsNullOrWhiteSpace(dispatched));

        // The attempt is kept with the release it chose and the plan it ran under.
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "series", $"SELECT COUNT(*) FROM series_search_history WHERE episode_id = '{episodeId}' AND trigger_kind = 'manual-episode';"));
        Assert.Equal(dispatched, await ApiScenes.TextAsync(rig.App, "series", $"SELECT release_name FROM series_search_history WHERE episode_id = '{episodeId}';"));
        var details = await ApiScenes.TextAsync(rig.App, "series", $"SELECT details_json FROM series_search_history WHERE episode_id = '{episodeId}';");
        using var plan = JsonDocument.Parse(details!);
        Assert.Equal(JsonValueKind.Object, plan.RootElement.ValueKind);
        Assert.Contains("plan", details!, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string SeriesId, string EpisodeId)> InstalledEpisodeAsync(TitleSearchAndGrabRoutesAgainstRecordedAppsTests.Rig rig)
    {
        var root = await ApiScenes.LibraryRootAsync(rig.App, rig.ShowLibrary);
        // The library's profile is the plan both the import's evaluation of the file and the later search judge by.
        var profile = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, "/api/quality-profiles", new
        {
            name = "Shows, upgrading", mediaType = "tv", cutoffQuality = "WEB 2160p", allowedQualities = "SDTV,HDTV 720p,HDTV 1080p,WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "WEB 2160p", minGb = 0.0, maxGb = 0.0, minMb = 0.0, maxMb = 0.0 } }
        });
        (await rig.App.Client.PutAsJsonAsync($"/api/libraries/{rig.ShowLibrary}/quality-profile", new { qualityProfileId = profile.GetProperty("id").GetString() })).EnsureSuccessStatusCode();

        // A real show folder on disk, read in by the real existing-library import, which gives the file its own evaluation.
        var folder = Path.Combine(root, "Breaking Bad (2008)", "Season 01");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Breaking.Bad.S01E01.720p.HDTV.x264-GRP.mkv"), [1, 2, 3, 4]);
        using (var scope = rig.App.Services.CreateScope())
        {
            var import = scope.ServiceProvider.GetRequiredService<IExistingLibraryImportService>();
            var started = await import.StartAsync(rig.ShowLibrary, CancellationToken.None);
            await import.RunSliceAsync(started!.Run.Id, CancellationToken.None);
        }

        var series = rig.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;
        var episodeId = (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().EpisodeId;

        return (seriesId, episodeId);
    }

    [Fact]
    public async Task A_manual_episode_search_for_an_episode_that_has_a_file_records_the_send_as_replacement_authorised()
    {
        await using var rig = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.RigAsync();
        var (seriesId, episodeId) = await InstalledEpisodeAsync(rig);

        var answer = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId } });

        Assert.True(answer.GetProperty("matchedCount").GetInt32() >= 1, answer.ToString());
        Assert.Equal("manual", await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT started_by FROM download_dispatches WHERE entity_id = '{episodeId}';"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT replacement_authorized FROM download_dispatches WHERE entity_id = '{episodeId}';"));
    }

    [Fact]
    public async Task An_upgrade_search_compares_candidates_against_the_installed_files_own_stored_evaluation()
    {
        await using var rig = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.RigAsync();
        var (seriesId, episodeId) = await InstalledEpisodeAsync(rig);

        // The stored snapshot of the installed file is what the comparison reads as "the file you have".
        var known = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, mode = "choose" });
        var candidate = known.GetProperty("episodes")[0].GetProperty("candidates")[0];
        var comparison = candidate.GetProperty("preferenceComparison");
        Assert.Equal("hdtv-720p", comparison.GetProperty("current").GetProperty("families")[0].GetProperty("selectedLevelId").GetString());
        Assert.True(comparison.GetProperty("persistentImprovement").GetBoolean());
        Assert.DoesNotContain("has not been checked yet", candidate.GetProperty("decisionReasons").ToString(), StringComparison.Ordinal);

        // It is the stored snapshot (this file's path and size, the plan it was judged under), not a fresh guess.
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "series", "SELECT COUNT(*) FROM media_preference_evaluations WHERE file_size_bytes = 4 AND file_path LIKE '%720p.HDTV%';"));
        Assert.Equal(
            await ApiScenes.TextAsync(rig.App, "series", "SELECT plan_id FROM media_preference_evaluations;"),
            comparison.GetProperty("current").GetProperty("planId").GetString());
    }
}
