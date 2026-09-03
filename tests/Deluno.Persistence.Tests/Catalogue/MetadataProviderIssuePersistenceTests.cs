using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using MovieRepository = Deluno.Movies.Data.SqliteMovieCatalogRepository;
using SeriesRepository = Deluno.Series.Data.SqliteSeriesCatalogRepository;

namespace Deluno.Persistence.Tests.Catalogue;

public sealed class MetadataProviderIssuePersistenceTests
{
    [Fact]
    public async Task Movie_acknowledgement_survives_same_evidence_and_new_identity_clears_it()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-01T01:00:00Z"));
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new MovieRepository(storage.Factory, clock);
        var movie = await repository.AddAsync(
            new CreateMovieRequest(
                "State of Siege: Temple Attack",
                2021,
                null,
                MetadataProvider: "tmdb",
                MetadataProviderId: "1603343"),
            CancellationToken.None);
        var evidence = new MetadataProviderIssue(
            "provider-record-missing", "tmdb", "1603343", "tmdb:movie:1603343:missing", clock.GetUtcNow(), null);

        var initialWriters = await Task.WhenAll(
            repository.RecordMetadataProviderIssueAsync(movie.Id, evidence, CancellationToken.None),
            repository.RecordMetadataProviderIssueAsync(movie.Id, evidence, CancellationToken.None));
        Assert.Equal(1, initialWriters.Count(result => result));
        clock.Advance(TimeSpan.FromHours(1));
        var acknowledged = await repository.AcknowledgeMetadataProviderIssueAsync(movie.Id, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await repository.RecordMetadataProviderIssueAsync(movie.Id, evidence with { DetectedUtc = clock.GetUtcNow() }, CancellationToken.None));

        var repeated = await repository.GetMetadataProviderIssueAsync(movie.Id, CancellationToken.None);
        Assert.Equal(acknowledged!.AcknowledgedUtc, repeated!.AcknowledgedUtc);
        Assert.Equal(evidence.DetectedUtc, repeated.DetectedUtc);

        await repository.UpdateMetadataAsync(movie.Id, Metadata("999", "Recovered title", "movies"), CancellationToken.None);
        Assert.Null(await repository.GetMetadataProviderIssueAsync(movie.Id, CancellationToken.None));
        Assert.NotNull(await repository.GetByIdAsync(movie.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Series_issue_is_title_scoped_and_does_not_remove_catalogue_entry()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-01T01:00:00Z"));
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SeriesRepository(storage.Factory, clock);
        var series = await repository.AddAsync(
            new CreateSeriesRequest(
                "A Removed Show",
                2020,
                null,
                MetadataProvider: "tmdb",
                MetadataProviderId: "42"),
            CancellationToken.None);

        var evidence = new MetadataProviderIssue(
            "provider-record-missing", "tmdb", "42", "tmdb:series:42:missing", clock.GetUtcNow(), null);
        var initialWriters = await Task.WhenAll(
            repository.RecordMetadataProviderIssueAsync(series.Id, evidence, CancellationToken.None),
            repository.RecordMetadataProviderIssueAsync(series.Id, evidence, CancellationToken.None));
        Assert.Equal(1, initialWriters.Count(result => result));

        Assert.NotNull(await repository.GetMetadataProviderIssueAsync(series.Id, CancellationToken.None));
        Assert.NotNull(await repository.GetByIdAsync(series.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_populated_movie_keeps_its_file_and_library_assignment_when_the_provider_record_is_gone()
    {
        // An empty catalogue row surviving a 404 proves very little: the row is
        // the only thing there is to lose. What #357 promises is that the media
        // survives - the file on disk Deluno is tracking, and the library that
        // says the file is Deluno's business. So the fixture is populated.
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-01T01:00:00Z"));
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new MovieRepository(storage.Factory, clock);

        const string libraryId = "library-films";
        const string filePath = @"C:\Library\Movies\State of Siege (2021)\State of Siege.mkv";
        Assert.True(await repository.ImportExistingAsync(
            libraryId: libraryId,
            title: "State of Siege: Temple Attack",
            releaseYear: 2021,
            wantedStatus: WantedStatuses.Covered,
            wantedReason: "imported",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            unmonitorWhenCutoffMet: false,
            filePath: filePath,
            fileSizeBytes: 8_123_456_789,
            cancellationToken: CancellationToken.None));

        var movie = Assert.Single(
            (await repository.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        await repository.UpdateMetadataAsync(
            movie.Id,
            Metadata("1603343", "State of Siege: Temple Attack", "movies"),
            CancellationToken.None);

        var evidence = new MetadataProviderIssue(
            "provider-record-missing", "tmdb", "1603343", "tmdb:movie:1603343:missing", clock.GetUtcNow(), null);
        Assert.True(await repository.RecordMetadataProviderIssueAsync(movie.Id, evidence, CancellationToken.None));

        var after = Assert.Single(
            (await repository.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Equal(movie.Id, after.Id);
        Assert.True(after.Monitored);
        Assert.True(after.HasFile);
        Assert.Equal(filePath, after.FilePath);
        Assert.Equal(8_123_456_789, after.FileSizeBytes);
        Assert.Equal(libraryId, after.LibraryId);
        Assert.Equal(WantedStatuses.Covered, after.WantedStatus);
        Assert.Equal("1603343", after.MetadataProviderId);

        // The tracked-file stream is what the rest of Deluno reads to decide a
        // file is its business. Losing the row here is how a "kept" title
        // silently stops being managed.
        var tracked = new List<MovieTrackedFileItem>();
        await foreach (var item in repository.StreamTrackedFilesAsync(libraryId, CancellationToken.None))
        {
            tracked.Add(item);
        }

        var trackedFile = Assert.Single(tracked);
        Assert.Equal(movie.Id, trackedFile.MovieId);
        Assert.Equal(libraryId, trackedFile.LibraryId);
        Assert.Equal(filePath, trackedFile.FilePath);
        Assert.Equal(8_123_456_789, trackedFile.FileSizeBytes);

        // And the notice itself is there to be resolved, not merely implied.
        Assert.NotNull(await repository.GetMetadataProviderIssueAsync(movie.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_populated_series_keeps_its_episode_file_and_library_assignment_when_the_provider_record_is_gone()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-01T01:00:00Z"));
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SeriesRepository(storage.Factory, clock);

        const string libraryId = "library-shows";
        const string filePath = @"C:\Library\Shows\A Removed Show (2020)\S01E01.mkv";
        Assert.True(await repository.ImportExistingAsync(
            libraryId: libraryId,
            title: "A Removed Show",
            startYear: 2020,
            wantedStatus: WantedStatuses.Covered,
            wantedReason: "imported",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            unmonitorWhenCutoffMet: false,
            filePath: filePath,
            fileSizeBytes: 1_234_567_890,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: filePath, FileSizeBytes: 1_234_567_890)],
            cancellationToken: CancellationToken.None));

        var series = Assert.Single(await repository.ListAsync(CancellationToken.None));
        await repository.UpdateMetadataAsync(
            series.Id,
            Metadata("999999999", "A Removed Show", "tv"),
            CancellationToken.None);

        var evidence = new MetadataProviderIssue(
            "provider-record-missing", "tmdb", "999999999", "tmdb:series:999999999:missing", clock.GetUtcNow(), null);
        Assert.True(await repository.RecordMetadataProviderIssueAsync(series.Id, evidence, CancellationToken.None));

        var after = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Equal(series.Id, after.Id);
        Assert.True(after.Monitored);
        Assert.Equal("999999999", after.MetadataProviderId);

        var tracked = new List<SeriesTrackedFileItem>();
        await foreach (var item in repository.StreamTrackedFilesAsync(libraryId, CancellationToken.None))
        {
            tracked.Add(item);
        }

        // A show streams a series-level row and an episode-level row for the
        // same file; both must still be attributed to this library.
        Assert.NotEmpty(tracked);
        Assert.All(tracked, item =>
        {
            Assert.Equal(series.Id, item.SeriesId);
            Assert.Equal(libraryId, item.LibraryId);
            Assert.Equal(filePath, item.FilePath);
            Assert.Equal(1_234_567_890, item.FileSizeBytes);
        });

        var episodeFile = Assert.Single(tracked, item => !string.IsNullOrEmpty(item.EpisodeId));
        Assert.Equal(1, episodeFile.SeasonNumber);
        Assert.Equal(1, episodeFile.EpisodeNumber);

        Assert.NotNull(await repository.GetMetadataProviderIssueAsync(series.Id, CancellationToken.None));
    }

    private static MetadataSearchResult Metadata(string id, string title, string mediaType)
        => new("tmdb", id, mediaType, title, null, 2021, "overview", null, null, 7, [], [], null, null);
}
