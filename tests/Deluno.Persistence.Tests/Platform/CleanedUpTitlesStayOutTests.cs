using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #492: a title a retention rule cleaned up does not come back through an
/// import list - any list of that media type, not just the one that added it -
/// and the list says why it is being skipped.
/// </summary>
public sealed class CleanedUpTitlesStayOutTests
{
    [Fact]
    public async Task Every_list_of_that_media_type_skips_a_cleaned_up_title()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var exclusions = new SqliteUnifiedExclusionRepository(storage.Factory, clock);
        var intake = new SqliteIntakeRepository(storage.Factory, clock, exclusions);
        var libraries = new SqliteLibrariesRepository(storage.Factory, clock);
        var movies = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest("Movies", "movies", "General", @"D:\Media\Movies", null, null, "standard", null, null, null, null, true, true, true, 6, 24, 25),
            CancellationToken.None);
        var list = await intake.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest("Classics", "imdb", "https://www.imdb.com/list/ls000000001/", "movies", movies.Id, null, null, null, null, null, null, "any", 24, true, true),
            CancellationToken.None);

        await exclusions.UpsertAsync(
            new UpsertMediaExclusionRequest("movies", MediaExclusionSourceKinds.Retention, "rule-1", "Watched films", "tmdb:653",
                "imdb:tt0013442", "Nosferatu", 1922, "tt0013442", null, "Cleaned up by retention rule “Watched films” on 17 Sep 2026 (Watched)"),
            CancellationToken.None);
        await exclusions.UpsertAsync(
            new UpsertMediaExclusionRequest("tv", MediaExclusionSourceKinds.Retention, "rule-2", "Watched shows", "retention",
                "title:pioneer one:2010", "Pioneer One", 2010, null, null, "Cleaned up by retention rule “Watched shows”"),
            CancellationToken.None);

        var skipped = await intake.ListActiveIntakeListExclusionsAsync(list!.Id, CancellationToken.None);

        var nosferatu = Assert.Single(skipped);
        Assert.Equal("imdb:tt0013442", nosferatu.EntryKey);
        Assert.StartsWith("Cleaned up by retention rule “Watched films”", nosferatu.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tt0013442", null, null, null, true)]
    [InlineData(null, "653", null, null, true)]
    [InlineData(null, null, "Nosferatu", 1922, true)]
    [InlineData(null, null, "Nosferatu", 1979, false)]
    [InlineData("tt0079641", "653", "Nosferatu", 1922, false)]
    public void An_exclusion_names_a_title_by_its_strongest_shared_identity(string? imdb, string? tmdb, string? title, int? year, bool expected)
    {
        var exclusion = new MediaExclusionItem("x", "movies", MediaExclusionSourceKinds.Retention, "rule-1", "Watched films", "tmdb:653",
            "imdb:tt0013442", "Nosferatu", 1922, "tt0013442", "Cleaned up", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, MediaExclusionMatching.Names(exclusion, imdb, tmdb, title, year));
    }
}
