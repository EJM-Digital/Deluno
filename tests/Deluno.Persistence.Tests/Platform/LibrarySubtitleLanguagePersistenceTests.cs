using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// Which subtitle languages a library wants (#301, DESIGN-002).
///
/// Per library, because that is what Deluno has and Bazarr and Weir do not:
/// one global list cannot say "English on everything, Japanese on anime".
/// </summary>
public sealed class LibrarySubtitleLanguagePersistenceTests
{
    private static async Task<SqliteLibrariesRepository> CreateRepositoryAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteLibrariesRepository(storage.Factory, clock);
    }

    private static CreateLibraryRequest NewLibrary(string name, string mediaType) => new(
        Name: name,
        MediaType: mediaType,
        Purpose: name,
        RootPath: $@"C:\Media\{name}",
        DownloadsPath: null,
        QualityProfileId: null,
        ImportWorkflow: "direct",
        ProcessorName: null,
        ProcessorOutputPath: null,
        ProcessorTimeoutMinutes: null,
        ProcessorFailureMode: null,
        AutoSearchEnabled: true,
        MissingSearchEnabled: true,
        UpgradeSearchEnabled: true,
        SearchIntervalHours: null,
        RetryDelayHours: null,
        MaxItemsPerRun: null);

    [Fact]
    public async Task A_library_starts_wanting_no_subtitles()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);

        var created = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        // Nothing wanted is the honest default, and a title that wants nothing
        // draws no bar at all — so nobody's shelf changes until they ask.
        Assert.Empty(created.SubtitleLanguages ?? []);
        Assert.Equal("all", created.SubtitleLanguageMode);
    }

    [Fact]
    public async Task Each_library_keeps_its_own_languages()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);

        var movies = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);
        var anime = await repository.CreateLibraryAsync(NewLibrary("Anime", "tv"), CancellationToken.None);

        await repository.UpdateLibrarySubtitlesAsync(
            movies.Id, new UpdateLibrarySubtitlesRequest(["en"], "all"), CancellationToken.None);
        var updatedAnime = await repository.UpdateLibrarySubtitlesAsync(
            anime.Id, new UpdateLibrarySubtitlesRequest(["en", "ja"], "all"), CancellationToken.None);

        Assert.NotNull(updatedAnime);
        Assert.Equal(["en", "ja"], updatedAnime!.SubtitleLanguages);

        // "English on everything, Japanese on anime" — the sentence a single
        // global list cannot say, and the reason this lives on the library.
        var libraries = await repository.ListLibrariesAsync(CancellationToken.None);
        Assert.Equal(["en"], libraries.Single(item => item.Id == movies.Id).SubtitleLanguages);
        Assert.Equal(["en", "ja"], libraries.Single(item => item.Id == anime.Id).SubtitleLanguages);
    }

    /// <summary>
    /// Order is the preference, and under <c>first</c> it is the whole meaning
    /// of "the first one you can get".
    /// </summary>
    [Fact]
    public async Task Language_order_is_preserved_and_the_mode_round_trips()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        var updated = await repository.UpdateLibrarySubtitlesAsync(
            library.Id, new UpdateLibrarySubtitlesRequest(["es", "en", "fr"], "first"), CancellationToken.None);

        Assert.Equal(["es", "en", "fr"], updated!.SubtitleLanguages);
        Assert.Equal("first", updated.SubtitleLanguageMode);
    }

    [Fact]
    public async Task Unknown_language_and_embedded_treatment_round_trip_through_library_reads()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        var updated = await repository.UpdateLibrarySubtitlesAsync(
            library.Id,
            new UpdateLibrarySubtitlesRequest(["en"], "all", UnknownLanguage: "eng", EmbeddedCounts: false),
            CancellationToken.None);

        Assert.Equal("en", updated!.SubtitleUnknownLanguage);
        Assert.False(updated.SubtitleEmbeddedCounts);

        var listed = (await repository.ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == library.Id);
        Assert.Equal("en", listed.SubtitleUnknownLanguage);
        Assert.False(listed.SubtitleEmbeddedCounts);
    }

    [Fact]
    public async Task Existing_library_defaults_keep_embedded_subtitles_held_and_unknown_names_unassigned()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        var listed = (await repository.ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == library.Id);

        Assert.Equal(string.Empty, listed.SubtitleUnknownLanguage);
        Assert.True(listed.SubtitleEmbeddedCounts);
    }

    [Fact]
    public async Task Subtitle_content_policy_round_trips_and_disabled_policy_is_not_persisted()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        var policy = new SubtitleContentModificationPolicy(
            StripHearingImpairedAnnotations: true,
            RemoveStyleTags: true,
            NormalizeWhitespace: true);
        var updated = await repository.UpdateLibrarySubtitlesAsync(
            library.Id,
            new UpdateLibrarySubtitlesRequest(["en"], "all", ContentPolicy: policy),
            CancellationToken.None);

        Assert.Equal(policy, updated!.SubtitleContentPolicy);
        var listed = (await repository.ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == library.Id);
        Assert.Equal(policy, listed.SubtitleContentPolicy);

        var cleared = await repository.UpdateLibrarySubtitlesAsync(
            library.Id,
            new UpdateLibrarySubtitlesRequest(["en"], "all", ContentPolicy: new SubtitleContentModificationPolicy()),
            CancellationToken.None);

        Assert.Null(cleared!.SubtitleContentPolicy);
    }

    [Fact]
    public async Task Subtitle_timing_policy_round_trips_with_safe_bounds_and_provider_exclusions()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        var updated = await repository.UpdateLibrarySubtitlesAsync(
            library.Id,
            new UpdateLibrarySubtitlesRequest(
                ["en"],
                "all",
                TimingPolicy: new SubtitleTimingPolicy(
                    Enabled: false,
                    SyncOnlyBelow: "same-source",
                    MaxOffsetSeconds: 999,
                    RequiredPeakSigma: 99,
                    ExcludedProviders: [" ProviderB ", "providerA", "PROVIDERA"])),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.NotNull(updated!.SubtitleTimingPolicy);
        Assert.False(updated.SubtitleTimingPolicy!.Enabled);
        Assert.Equal("same-source", updated.SubtitleTimingPolicy.SyncOnlyBelow);
        Assert.Equal(300, updated.SubtitleTimingPolicy.MaxOffsetSeconds);
        Assert.Equal(10, updated.SubtitleTimingPolicy.RequiredPeakSigma);
        Assert.Equal(["providera", "providerb"], updated.SubtitleTimingPolicy.ExcludedProviders);

        var listed = (await repository.ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == library.Id);

        Assert.NotNull(listed.SubtitleTimingPolicy);
        Assert.Equal(updated.SubtitleTimingPolicy.Enabled, listed.SubtitleTimingPolicy!.Enabled);
        Assert.Equal(updated.SubtitleTimingPolicy.SyncOnlyBelow, listed.SubtitleTimingPolicy.SyncOnlyBelow);
        Assert.Equal(updated.SubtitleTimingPolicy.MaxOffsetSeconds, listed.SubtitleTimingPolicy.MaxOffsetSeconds);
        Assert.Equal(updated.SubtitleTimingPolicy.RequiredPeakSigma, listed.SubtitleTimingPolicy.RequiredPeakSigma);
        Assert.Equal(updated.SubtitleTimingPolicy.ExcludedProviders, listed.SubtitleTimingPolicy.ExcludedProviders);
    }

    /// <summary>
    /// The bar under a poster counts these, so a duplicate or a stray case would
    /// inflate what it claims was asked for — and no title can be twice
    /// subtitled in one language.
    /// </summary>
    [Fact]
    public async Task Duplicates_and_casing_cannot_inflate_what_was_asked_for()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        var updated = await repository.UpdateLibrarySubtitlesAsync(
            library.Id,
            new UpdateLibrarySubtitlesRequest([" EN ", "en", "Ja", "ja", ""], "all"),
            CancellationToken.None);

        Assert.Equal(["en", "ja"], updated!.SubtitleLanguages);
    }

    /// <summary>
    /// An unrecognised mode reads as <c>all</c>. Guessing <c>first</c> would
    /// quietly stop fetching languages somebody had asked for, which is the
    /// dangerous direction — the same reason `NormalizeWantedStatus` was made
    /// loud rather than defaulting to "missing" (#300).
    /// </summary>
    [Fact]
    public async Task An_unknown_mode_wants_every_language_rather_than_one()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        var updated = await repository.UpdateLibrarySubtitlesAsync(
            library.Id, new UpdateLibrarySubtitlesRequest(["en", "ja"], "cutoff-position-3"), CancellationToken.None);

        Assert.Equal("all", updated!.SubtitleLanguageMode);
    }

    [Fact]
    public async Task Clearing_the_languages_takes_the_bar_away_again()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateRepositoryAsync(storage);
        var library = await repository.CreateLibraryAsync(NewLibrary("Movies", "movies"), CancellationToken.None);

        await repository.UpdateLibrarySubtitlesAsync(
            library.Id, new UpdateLibrarySubtitlesRequest(["en"], "all"), CancellationToken.None);
        var cleared = await repository.UpdateLibrarySubtitlesAsync(
            library.Id, new UpdateLibrarySubtitlesRequest([], "all"), CancellationToken.None);

        Assert.Empty(cleared!.SubtitleLanguages ?? []);
    }
}
