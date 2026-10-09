using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #1159: a search that ended "held" leaves a usable release waiting for a person's yes. Transfers lists it under
/// Needs action with Approve / Find another / Dismiss, and it stops being listed once answered or once the title has
/// its file. Approving needs the release's link, so the stored plan keeps it for the held candidate.
/// </summary>
public sealed class HeldReleaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T09:00:00Z");

    private const string HeldPlan =
        """{"bestCandidate":{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","indexerName":"Nyaa","indexerId":"ix1","quality":"WEB 2160p","sizeBytes":8000000000,"seeders":12,"downloadUrl":"https://indexer.example/get/1","decisionStatus":"held","summary":"Held for review by the typed release plan."},"candidates":[{"releaseName":"Big.Buck.Bunny.2008.2160p.WEB-DL-GRP","decisionStatus":"held","downloadUrl":"https://indexer.example/get/1"}]}""";

    private static async Task<(TestStorage Storage, SqliteMovieCatalogRepository Movies)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (storage, new SqliteMovieCatalogRepository(storage.Factory, time));
    }

    private static async Task<string> HeldMovieAsync(SqliteMovieCatalogRepository movies, string title, string outcome = "held", string? plan = HeldPlan)
    {
        var added = await movies.AddAsync(new CreateMovieRequest(title, 2008, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await movies.RecordSearchAttemptAsync(added.Id, "lib", "automatic", outcome, Now.AddHours(-1), null, "Held.", "Big.Buck.Bunny.2008.2160p.WEB-DL-GRP", "Nyaa", plan, CancellationToken.None);
        return added.Id;
    }

    [Fact]
    public async Task A_held_search_lists_its_candidate_with_the_link_the_approval_will_send()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        var id = await HeldMovieAsync(movies, "Big Buck Bunny");
        await HeldMovieAsync(movies, "Grabbed", outcome: "matched");

        var item = Assert.Single(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));

        Assert.Equal(id, item.Id);
        Assert.Equal("movie", item.MediaType);
        Assert.Equal("Big.Buck.Bunny.2008.2160p.WEB-DL-GRP", item.ReleaseName);
        Assert.Equal("https://indexer.example/get/1", item.DownloadUrl);
        Assert.Equal("ix1", item.IndexerId);
        Assert.Equal("WEB 2160p", item.Quality);
    }

    [Fact]
    public async Task A_refused_candidate_is_never_listed_as_waiting_for_approval()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        await HeldMovieAsync(movies, "Wrong Show", plan: HeldPlan.Replace("\"decisionStatus\":\"held\",\"summary\"", "\"decisionStatus\":\"rejected\",\"summary\""));

        Assert.Empty(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));
    }

    [Fact]
    public async Task A_later_search_that_sent_something_takes_it_off_the_list()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        var id = await HeldMovieAsync(movies, "Big Buck Bunny");
        Assert.Single(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));

        await movies.RecordSearchAttemptAsync(id, "lib", "automatic", "matched", Now, null, "Sent.", null, null, null, CancellationToken.None);

        Assert.Empty(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));
    }

    [Fact]
    public async Task The_stored_plan_keeps_the_held_candidates_link_when_a_search_with_many_releases_is_trimmed()
    {
        var many = string.Join(",", Enumerable.Range(0, 20).Select(index => $$"""{"releaseName":"R{{index}}","decisionStatus":"held","downloadUrl":"https://indexer.example/get/{{index}}"}"""));
        var plan = """{"bestCandidate":{"releaseName":"Many.Releases.2008.1080p.WEB-GRP","indexerName":"Nyaa","decisionStatus":"held","downloadUrl":"https://indexer.example/get/best"},"candidates":[""" + many + "]}";
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        await HeldMovieAsync(movies, "Many Releases", plan: plan);

        var item = Assert.Single(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));

        Assert.Equal("https://indexer.example/get/best", item.DownloadUrl);
    }

    [Fact]
    public async Task A_film_that_has_its_file_since_is_not_listed_and_approving_it_is_refused_in_plain_words()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        var id = await HeldMovieAsync(movies, "Big Buck Bunny");
        var reader = new SqliteHeldReleaseReader(storage.Factory);
        var held = Assert.Single(await reader.ListAsync(50, CancellationToken.None));

        await movies.EnsureWantedStateAsync(id, "lib", "covered", "Has its file", hasFile: true, "WEB 2160p", "WEB 1080p", true, CancellationToken.None);

        Assert.Empty(await reader.ListAsync(50, CancellationToken.None));
        var check = await reader.CheckAsync(held.HistoryId, CancellationToken.None);
        Assert.False(check!.IsOffered);
        Assert.Contains("no longer needs a release", check.Refusal);
    }

    [Fact]
    public async Task A_film_release_the_title_rules_refuse_now_is_not_listed_and_approving_it_is_refused()
    {
        var (storage, movies) = await CreateAsync();
        using var _ = storage;
        await HeldMovieAsync(movies, "Something Else Entirely");
        var reader = new SqliteHeldReleaseReader(storage.Factory);

        Assert.Empty(await reader.ListAsync(50, CancellationToken.None));
    }

    private const string SimpsonsPlan =
        """{"bestCandidate":{"releaseName":"The.Simpsons.S38E02.The.Childrens.Book.Job.1080p.DSNP.WEB-DL.DDP5.1.H.264-FLUX","indexerName":"EZTV","indexerId":"ix2","quality":"WEB 1080p","sizeBytes":719000000,"seeders":9,"downloadUrl":"https://indexer.example/get/2","decisionStatus":"held","summary":"Eligible under the typed release plan."}}""";

    private static async Task<(string SeriesId, string[] EpisodeIds, SqliteSeriesCatalogRepository Series)> HeldSeriesAsync(TestStorage storage, string wantedStatusOfEpisode2, string plan = SimpsonsPlan)
    {
        var time = new FixedTimeProvider(Now);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, time);
        var added = await series.AddAsync(new CreateSeriesRequest("The Simpsons", 1989, null), CancellationToken.None);
        var ids = new List<string>();
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None))
        {
            foreach (var (number, title, status) in new[] { (1, "The Childrens Book Job", "covered"), (2, "Another Story Altogether", wantedStatusOfEpisode2) })
            {
                var episodeId = Guid.CreateVersion7().ToString("N");
                ids.Add(episodeId);
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO episode_entries (id, series_id, season_number, episode_number, title, monitored, has_file, created_utc, updated_utc)
                    VALUES (@id, @series, 38, @number, @title, 1, 0, @now, @now);
                    INSERT INTO episode_wanted_state (episode_id, series_id, library_id, wanted_status, wanted_reason, updated_utc)
                    VALUES (@id, @series, 'lib', @status, 'test', @now);
                    """;
                foreach (var (name, value) in new (string, object)[] { ("@id", episodeId), ("@series", added.Id), ("@number", number), ("@title", title), ("@status", status), ("@now", Now.ToString("O")) })
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                }

                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        await series.RecordSearchAttemptAsync(added.Id, ids[1], "lib", "automatic", "held", Now.AddHours(-1), null, "Held.", "name", "EZTV", plan, CancellationToken.None);
        return (added.Id, [.. ids], series);
    }

    [Fact]
    public async Task An_episode_that_has_its_file_since_is_not_listed()
    {
        var (storage, _) = await CreateAsync();
        using var _ = storage;
        // Episode 2 is covered. The held release was written by an older build.
        await HeldSeriesAsync(storage, "covered", SimpsonsPlan.Replace("S38E02.The.Childrens.Book.Job", "S38E02.Another.Story.Altogether"));

        Assert.Empty(await new SqliteHeldReleaseReader(storage.Factory).ListAsync(50, CancellationToken.None));
    }

    [Fact]
    public async Task A_held_release_carrying_another_episodes_title_is_not_listed_and_approving_it_gives_the_reason()
    {
        var (storage, _) = await CreateAsync();
        using var _ = storage;
        // Episode 2 still missing, but the release names S38E02 with the title of S38E01.
        var (_, _, series) = await HeldSeriesAsync(storage, "missing");
        var reader = new SqliteHeldReleaseReader(storage.Factory);

        Assert.Empty(await reader.ListAsync(50, CancellationToken.None));
        var historyId = (await reader.CheckAsync(await FirstHistoryIdAsync(storage), CancellationToken.None))!;
        Assert.False(historyId.IsOffered);
        Assert.False(string.IsNullOrWhiteSpace(historyId.Refusal));
    }

    [Fact]
    public async Task A_still_valid_held_episode_is_listed_and_approvable()
    {
        var (storage, _) = await CreateAsync();
        using var _ = storage;
        await HeldSeriesAsync(storage, "missing", SimpsonsPlan.Replace("S38E02.The.Childrens.Book.Job", "S38E02.Another.Story.Altogether"));
        var reader = new SqliteHeldReleaseReader(storage.Factory);

        var item = Assert.Single(await reader.ListAsync(50, CancellationToken.None));

        Assert.Equal("S38E02", item.EpisodeLabel);
        var check = await reader.CheckAsync(item.HistoryId, CancellationToken.None);
        Assert.True(check!.IsOffered);
        Assert.Null(check.Refusal);
    }

    private static async Task<string> FirstHistoryIdAsync(TestStorage storage)
    {
        await using var connection = await storage.Factory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM series_search_history WHERE outcome = 'held' LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task An_answered_held_release_is_forgotten_only_when_it_stops_being_held_and_never_by_the_other_list()
    {
        var (storage, _) = await CreateAsync();
        using var _ = storage;
        var store = new AttentionDismissalStore(storage.Factory, new FixedTimeProvider(Now));
        var held = AttentionDismissalStore.HeldReleaseKey("h1");
        var noRelease = AttentionDismissalStore.NoReleaseKey("movie", "m1");
        await store.DismissAsync(held, CancellationToken.None);
        await store.DismissAsync(noRelease, CancellationToken.None);

        // The no-release list pruning itself leaves the held dismissal alone.
        await store.ForgetExceptAsync(new HashSet<string>(), CancellationToken.None);
        Assert.Equal([held], (await store.ListAsync(CancellationToken.None)).ToArray());

        await store.ForgetExceptAsync(new HashSet<string>(), CancellationToken.None, AttentionDismissalStore.HeldReleasePrefix);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_dismissed_release_offered_again_by_a_new_search_stays_dismissed_until_the_title_is_held_for_another_release()
    {
        var (storage, _) = await CreateAsync();
        using var _ = storage;
        var store = new AttentionDismissalStore(storage.Factory, new FixedTimeProvider(Now));
        await store.DismissAsync(AttentionDismissalStore.HeldReleaseKey("h1"), CancellationToken.None);
        await store.DismissAsync(AttentionDismissalStore.HeldReleaseNameKey("movie", "m1", null, "Profhigh.2020.1080p"), CancellationToken.None);

        // The feed watch offers the same release again: a new row (h2), the same release of the same title.
        var dismissed = await store.ListAsync(CancellationToken.None);
        Assert.True(AttentionDismissalStore.IsHeldReleaseDismissed(dismissed, "h2", "movie", "m1", null, "profhigh.2020.1080P"));
        Assert.False(AttentionDismissalStore.IsHeldReleaseDismissed(dismissed, "h2", "movie", "m2", null, "Profhigh.2020.1080p"));
        Assert.False(AttentionDismissalStore.IsHeldReleaseDismissed(dismissed, "h2", "movie", "m1", null, "Profhigh.2020.2160p"));

        // Nothing is held for the title now: the dismissal is kept. A different release held for it is new, and the old dismissal goes.
        await store.ForgetSupersededHeldReleasesAsync([("movie", "m3", null, "Other")], CancellationToken.None);
        Assert.Contains(AttentionDismissalStore.HeldReleaseNameKey("movie", "m1", null, "Profhigh.2020.1080p"), await store.ListAsync(CancellationToken.None));
        await store.ForgetSupersededHeldReleasesAsync([("movie", "m1", null, "Profhigh.2020.2160p")], CancellationToken.None);
        Assert.DoesNotContain(AttentionDismissalStore.HeldReleaseNameKey("movie", "m1", null, "Profhigh.2020.1080p"), await store.ListAsync(CancellationToken.None));
    }
}
