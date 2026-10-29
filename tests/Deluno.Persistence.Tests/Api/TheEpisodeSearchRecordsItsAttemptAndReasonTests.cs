using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What a manual episode search records and answers (coverage register, engines), against Jackett 0.24.2850's real Torznab results
/// for Breaking Bad S01E01 served over a loopback port, and qBittorrent 5.2.1 taking the add.
/// </summary>
public sealed class TheEpisodeSearchRecordsItsAttemptAndReasonTests
{
    [Fact]
    public async Task Each_searched_episode_gets_a_manual_episode_history_row_with_a_retry_after_and_the_reason_says_ok_when_sent_and_held_for_review_when_only_compared()
    {
        await using var rig = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.RigAsync();
        var (seriesId, episodeId) = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.AddShowAsync(rig);

        // Choose mode compares the real releases and sends nothing: the overall reason is held_for_review, the row says "checked".
        var chosen = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, mode = "choose" });
        Assert.Equal("held_for_review", chosen.GetProperty("reason").GetString());
        Assert.Equal(0, chosen.GetProperty("sentCount").GetInt32());
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "series", $"SELECT COUNT(*) FROM series_search_history WHERE episode_id = '{episodeId}' AND trigger_kind = 'manual-episode' AND outcome = 'checked';"));
        var retryAfter = await ApiScenes.TextAsync(rig.App, "series", $"SELECT next_eligible_search_utc FROM series_wanted_state WHERE series_id = '{seriesId}';");
        Assert.True(DateTimeOffset.Parse(retryAfter!) > DateTimeOffset.UtcNow);
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", "SELECT COUNT(*) FROM activity_events WHERE category = 'series.search.episode';"));

        // Sending it for real: something matched, so the reason is ok.
        var sent = await TitleSearchAndGrabRoutesAgainstRecordedAppsTests.PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId } });
        Assert.Equal("ok", sent.GetProperty("reason").GetString());
        Assert.True(sent.GetProperty("matchedCount").GetInt32() >= 1);
        Assert.Equal(2, await ApiScenes.ScalarAsync(rig.App, "series", $"SELECT COUNT(*) FROM series_search_history WHERE episode_id = '{episodeId}' AND trigger_kind = 'manual-episode';"));
    }
}
