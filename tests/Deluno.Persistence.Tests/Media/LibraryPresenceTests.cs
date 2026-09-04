using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// The Add screen says you already have it (#424).
///
/// <para><b>What was missing was never the safety.</b> Adding a title Deluno
/// already holds has been a no-op that hands back the existing row for a long
/// time - three unique indexes and one matcher see to that. But nothing ever
/// said so, so the only way to find out you already owned something was to add
/// it and watch the catalogue not grow.</para>
///
/// <para>These tests hold the marker to the matcher: whatever
/// <see cref="IMediaStateRepository.AddAsync"/> would collapse, the Add screen
/// must already have said it holds. The two cannot be allowed to drift, which
/// is why the presence check runs the same query rather than forming a second
/// opinion about what "the same title" means.</para>
/// </summary>
public sealed class LibraryPresenceTests
{
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task A_title_already_held_is_marked_with_the_entry_it_would_land_on(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        await InitializeBothSchemasAsync(storage, timeProvider);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        var held = await repository.AddAsync(kind, Identity("Arrival", 2016, "tt2543164", "1234"), CancellationToken.None);

        var marked = await new MediaStateLibraryPresence(repository).MarkHeldTitlesAsync(
            [
                Result(kind, "Arrival", 2016, "tt2543164", "1234"),
                Result(kind, "Sicario", 2015, "tt3397884", "5678")
            ],
            CancellationToken.None);

        Assert.Equal(held, marked[0].LibraryEntryId);
        Assert.Null(marked[1].LibraryEntryId);
    }

    /// <summary>
    /// Every way the matcher can recognise a title, the marker recognises too.
    ///
    /// <para>The IMDb id and the provider id are the interesting ones: a result
    /// whose title reads nothing like the stored row is still the same film, and
    /// a marker that compared titles would call it new and offer to add a second
    /// copy the database would then silently refuse to make.</para>
    /// </summary>
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Every_match_the_catalogue_makes_is_a_match_the_add_screen_shows(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        await InitializeBothSchemasAsync(storage, timeProvider);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var presence = new MediaStateLibraryPresence(repository);

        var held = await repository.AddAsync(
            kind,
            Identity("Big Buck Bunny", 2008, "tt1254207", "10378"),
            CancellationToken.None);

        var marked = await presence.MarkHeldTitlesAsync(
            [
                // Nothing but the IMDb id in common.
                Result(kind, "Big Buck Bunny: Peach Open Movie", null, "tt1254207", "99999"),
                // Nothing but the provider id in common.
                Result(kind, "Peach Open Movie", null, null, "10378"),
                // A title arriving without its year still matches one that has
                // one - the gap #423 closed, held here on the reading side too.
                Result(kind, "big buck bunny", null, null, null),
                // A different year is a different title, not a duplicate.
                Result(kind, "Big Buck Bunny", 2019, null, null)
            ],
            CancellationToken.None);

        Assert.Equal(held, marked[0].LibraryEntryId);
        Assert.Equal(held, marked[1].LibraryEntryId);
        Assert.Equal(held, marked[2].LibraryEntryId);
        Assert.Null(marked[3].LibraryEntryId);
    }

    /// <summary>
    /// Films and shows live in different databases, and a result says which it
    /// is. Looking a show up in the movie catalogue would report every show as
    /// new - the mirror of the bug this fixes.
    /// </summary>
    [Fact]
    public async Task Films_and_shows_are_each_looked_up_in_their_own_catalogue()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        await InitializeBothSchemasAsync(storage, timeProvider);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        var film = await repository.AddAsync(
            MediaKind.Movie,
            Identity("Severance", 2016, null, "310131"),
            CancellationToken.None);
        var show = await repository.AddAsync(
            MediaKind.Series,
            Identity("Severance", 2022, null, "95396"),
            CancellationToken.None);

        var marked = await new MediaStateLibraryPresence(repository).MarkHeldTitlesAsync(
            [
                Result(MediaKind.Series, "Severance", 2022, null, "95396"),
                Result(MediaKind.Movie, "Severance", 2016, null, "310131"),
                Result(MediaKind.Series, "Severance", 2016, null, "310131")
            ],
            CancellationToken.None);

        Assert.Equal(show, marked[0].LibraryEntryId);
        Assert.Equal(film, marked[1].LibraryEntryId);
        // The film's identity, asked of the show catalogue. Not held there.
        Assert.Null(marked[2].LibraryEntryId);
        Assert.NotEqual(film, show);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task An_empty_catalogue_marks_nothing(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        await InitializeBothSchemasAsync(storage, timeProvider);
        var presence = new MediaStateLibraryPresence(new SqliteMediaStateRepository(storage.Factory, timeProvider));

        var marked = await presence.MarkHeldTitlesAsync(
            [Result(kind, "Arrival", 2016, "tt2543164", "1234")],
            CancellationToken.None);

        Assert.Null(Assert.Single(marked).LibraryEntryId);
        Assert.Empty(await presence.MarkHeldTitlesAsync([], CancellationToken.None));
    }

    /// <summary>
    /// The property that keeps the screen and the button saying one thing: the
    /// id the Add screen shows is the id an Add would return.
    /// </summary>
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task What_the_marker_shows_is_what_a_second_add_would_return(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        await InitializeBothSchemasAsync(storage, timeProvider);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        var first = await repository.AddAsync(kind, Identity("Arrival", 2016, "tt2543164", "1234"), CancellationToken.None);

        var marked = await new MediaStateLibraryPresence(repository).MarkHeldTitlesAsync(
            [Result(kind, "Arrival", 2016, "tt2543164", "1234")],
            CancellationToken.None);

        var addedAgain = await repository.AddAsync(kind, Identity("Arrival", 2016, "tt2543164", "1234"), CancellationToken.None);

        Assert.Equal(first, addedAgain);
        Assert.Equal(addedAgain, Assert.Single(marked).LibraryEntryId);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Asking_about_nothing_returns_nothing(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        await InitializeBothSchemasAsync(storage, timeProvider);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        Assert.Empty(await repository.FindExistingEntryIdsAsync(kind, [], CancellationToken.None));
    }

    // ------------------------------------------------------------------ helpers

    private static MediaEntryCreate Identity(string title, int? year, string? imdbId, string? providerId)
        => new(
            title,
            year,
            imdbId,
            Monitored: true,
            providerId is null ? null : "tmdb",
            providerId,
            OriginalTitle: null,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            Genres: null,
            ExternalUrl: null,
            MetadataJson: null);

    private static MetadataSearchResult Result(
        MediaKind kind,
        string title,
        int? year,
        string? imdbId,
        string? providerId)
        => new(
            "tmdb",
            providerId ?? string.Empty,
            kind == MediaKind.Series ? "tv" : "movies",
            title,
            OriginalTitle: null,
            year,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            [],
            [],
            imdbId,
            ExternalUrl: null);

    private static async Task InitializeBothSchemasAsync(TestStorage storage, TimeProvider timeProvider)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new MoviesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }
}
