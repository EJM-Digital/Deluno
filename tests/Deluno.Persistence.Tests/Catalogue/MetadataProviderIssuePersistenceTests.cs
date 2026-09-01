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

    private static MetadataSearchResult Metadata(string id, string title, string mediaType)
        => new("tmdb", id, mediaType, title, null, 2021, "overview", null, null, 7, [], [], null, null);
}
