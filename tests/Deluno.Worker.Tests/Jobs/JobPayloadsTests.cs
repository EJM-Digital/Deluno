using Deluno.Worker.Jobs;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>A job's payload is read the same way for every kind: a good payload is read in full, text that is not JSON is read as nothing, and a missing payload reads as an empty one.</summary>
public sealed class JobPayloadsTests
{
    [Fact]
    public void A_library_search_payload_is_read_in_full_whatever_the_case_of_its_names()
    {
        var payload = JobPayloads.ParseLibraryPayload("""
            {"libraryId":"lib-1","LIBRARYNAME":"Films","mediaType":"movies","checkMissing":true,"checkUpgrades":true,"maxItems":8,
             "retryDelayHours":6,"triggeredBy":"schedule","targetEntityId":"m-1","searchKind":"missing","scopeId":"s-1","scopeName":"Recent",
             "randomizeSearchOrder":true,"maxMissingItems":3,"maxUpgradeItems":2,"priorityIndexerIds":["i-1"],"followUp":1}
            """);

        Assert.NotNull(payload);
        Assert.Equal("lib-1", payload.LibraryId);
        Assert.Equal("Films", payload.LibraryName);
        Assert.Equal("movies", payload.MediaType);
        Assert.True(payload.CheckMissing);
        Assert.True(payload.CheckUpgrades);
        Assert.Equal(8, payload.MaxItems);
        Assert.Equal(6, payload.RetryDelayHours);
        Assert.Equal("schedule", payload.TriggeredBy);
        Assert.Equal("m-1", payload.TargetEntityId);
        Assert.Equal("missing", payload.SearchKind);
        Assert.Equal("s-1", payload.ScopeId);
        Assert.Equal("Recent", payload.ScopeName);
        Assert.True(payload.RandomizeSearchOrder);
        Assert.Equal(3, payload.EffectiveMaxMissingItems);
        Assert.Equal(2, payload.EffectiveMaxUpgradeItems);
        Assert.Equal(["i-1"], payload.PriorityIndexerIds);
        Assert.Equal(1, payload.FollowUp);
    }

    [Fact]
    public void An_older_library_search_payload_without_the_split_caps_uses_the_one_cap_for_both()
    {
        var payload = JobPayloads.ParseLibraryPayload("""{"maxItems":10}""");

        Assert.NotNull(payload);
        Assert.Equal(10, payload.EffectiveMaxMissingItems);
        Assert.Equal(10, payload.EffectiveMaxUpgradeItems);
        Assert.Equal("combined", payload.SearchKind);
    }

    [Fact]
    public void A_quality_payload_is_read_in_full()
    {
        var payload = JobPayloads.ParseQualityPayload("""{"libraryId":"lib-1","libraryName":"Films","mediaType":"movies","cutoffQuality":"1080p","upgradeUntilCutoff":true,"upgradeUnknownItems":true}""");

        Assert.NotNull(payload);
        Assert.Equal("lib-1", payload.LibraryId);
        Assert.Equal("Films", payload.LibraryName);
        Assert.Equal("movies", payload.MediaType);
        Assert.Equal("1080p", payload.CutoffQuality);
        Assert.True(payload.UpgradeUntilCutoff);
        Assert.True(payload.UpgradeUnknownItems);
    }

    [Fact]
    public void An_episode_search_payload_is_read_in_full()
    {
        var payload = JobPayloads.ParseEpisodeSearchPayload("""{"episodeId":"e-1","seriesId":"s-1","libraryId":"lib-2","seasonNumber":2,"episodeNumber":5,"title":"Pilot"}""");

        Assert.NotNull(payload);
        Assert.Equal("e-1", payload.EpisodeId);
        Assert.Equal("s-1", payload.SeriesId);
        Assert.Equal("lib-2", payload.LibraryId);
        Assert.Equal(2, payload.SeasonNumber);
        Assert.Equal(5, payload.EpisodeNumber);
        Assert.Equal("Pilot", payload.Title);
    }

    [Fact]
    public void A_season_search_payload_is_read_with_its_episodes()
    {
        var payload = JobPayloads.ParseSeasonSearchPayload("""{"seriesId":"s-1","seriesTitle":"Show","libraryId":"lib-2","seasonNumber":3,"episodes":[{"episodeId":"e-1","episodeNumber":1,"title":"One"}]}""");

        Assert.NotNull(payload);
        Assert.Equal("Show", payload.SeriesTitle);
        Assert.Equal(3, payload.SeasonNumber);
        var episode = Assert.Single(payload.Episodes);
        Assert.Equal("e-1", episode.EpisodeId);
        Assert.Equal(1, episode.EpisodeNumber);
        Assert.Equal("One", episode.Title);
    }

    [Fact]
    public void A_catalogue_refresh_payload_is_read_with_the_search_it_will_queue_afterwards()
    {
        var payload = JobPayloads.ParseCatalogRefreshPayload("""
            {"id":"t-1","title":"Alien","imdbId":"tt0078748",
             "pendingSearches":[{"libraryId":"lib-1","libraryName":"Films","mediaType":"movies","retryDelayHours":4,"triggeredBy":"add","targetEntityId":"t-1"}]}
            """);

        Assert.NotNull(payload);
        Assert.Equal("t-1", payload.Id);
        Assert.Equal("Alien", payload.Title);
        Assert.Equal("tt0078748", payload.ImdbId);
        var search = Assert.Single(payload.PendingSearches!);
        Assert.Equal("lib-1", search.LibraryId);
        Assert.Equal("Films", search.LibraryName);
        Assert.Equal("movies", search.MediaType);
        Assert.Equal(4, search.RetryDelayHours);
        Assert.Equal("add", search.TriggeredBy);
        Assert.Equal("t-1", search.TargetEntityId);
    }

    [Fact]
    public void An_intake_sync_payload_is_read_in_full()
    {
        var payload = JobPayloads.ParseIntakeSyncPayload("""{"sourceId":"src-1","manual":true}""");

        Assert.NotNull(payload);
        Assert.Equal("src-1", payload.SourceId);
        Assert.True(payload.Manual);
    }

    [Fact]
    public void A_job_with_no_payload_reads_as_an_empty_one_for_every_kind()
    {
        Assert.NotNull(JobPayloads.ParseLibraryPayload(null));
        Assert.NotNull(JobPayloads.ParseQualityPayload(null));
        Assert.NotNull(JobPayloads.ParseEpisodeSearchPayload(null));
        Assert.NotNull(JobPayloads.ParseSeasonSearchPayload(null));
        Assert.NotNull(JobPayloads.ParseCatalogRefreshPayload(null));
        var intake = JobPayloads.ParseIntakeSyncPayload(null);
        Assert.NotNull(intake);
        Assert.Null(intake.SourceId);
        Assert.False(intake.Manual);
    }

    [Fact]
    public void Text_that_is_not_json_reads_as_nothing_for_every_kind()
    {
        Assert.Null(JobPayloads.ParseLibraryPayload("not json"));
        Assert.Null(JobPayloads.ParseQualityPayload("not json"));
        Assert.Null(JobPayloads.ParseEpisodeSearchPayload("not json"));
        Assert.Null(JobPayloads.ParseSeasonSearchPayload("not json"));
        Assert.Null(JobPayloads.ParseCatalogRefreshPayload("not json"));
        Assert.Null(JobPayloads.ParseIntakeSyncPayload("not json"));
    }
}
