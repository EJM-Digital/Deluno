using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// "This file is below the size rule for its own quality tier."
///
/// <para>#309's flagship, and the one nothing in the arr suite answers.
/// Cleanuparr handles stalled, slow and orphaned <i>downloads</i>; nothing
/// audits whether the files you already keep still match the rules you set. A
/// 2160p file sitting at 4 GB was accepted under a profile that says 2160p
/// should be 7–60 GB, and today finding it means a spreadsheet.</para>
/// </summary>
public sealed class ConformanceFilterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-29T00:00:00Z");

    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>A 2160p rule of 7–60 GB, and nothing else judged.</summary>
    private static readonly QualityTierDefinition[] Ladder =
    [
        new("WEB 2160p", 100, MovieMinGb: 7, MovieMaxGb: 60, EpisodeMinMb: 0, EpisodeMaxMb: 0, ScoreCeiling: 0),
        // No bounds at all, to prove "no rule" is not the same as "breaches".
        new("WEB 1080p", 70, MovieMinGb: 0, MovieMaxGb: 0, EpisodeMinMb: 0, EpisodeMaxMb: 0, ScoreCeiling: 0)
    ];

    [Fact]
    public async Task Both_ends_of_the_rule_are_found_and_a_conforming_file_is_not()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        await movies.SyncQualityRanksAsync(Ladder, CancellationToken.None);

        // Under the floor is a bad copy; over the ceiling is wasted disk. They
        // are different problems, which is why the verdict is not a boolean.
        await ImportAsync(movies, "Skinny", "WEB 2160p", 4 * Gb);
        await ImportAsync(movies, "Bloated", "WEB 2160p", 80 * Gb);
        await ImportAsync(movies, "Correct", "WEB 2160p", 20 * Gb);

        Assert.Equal(["Skinny"], await TitlesAsync(movies, "sizeConformance:in:under"));
        Assert.Equal(["Bloated"], await TitlesAsync(movies, "sizeConformance:in:over"));
        Assert.Equal(["Correct"], await TitlesAsync(movies, "sizeConformance:in:ok"));
    }

    /// <summary>
    /// A tier with no rule, and a title with no file, are both unjudged — and
    /// unjudged is not the same as compliant.
    /// </summary>
    [Fact]
    public async Task What_cannot_be_judged_is_not_quietly_called_compliant()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        await movies.SyncQualityRanksAsync(Ladder, CancellationToken.None);

        await ImportAsync(movies, "Unruled", "WEB 1080p", 2 * Gb);
        await movies.AddAsync(new CreateMovieRequest("Empty", 2016, null), CancellationToken.None);

        // Answering 'ok' here would count every title with no rule and every
        // title with no file as passing an audit they were never in.
        Assert.Empty(await TitlesAsync(movies, "sizeConformance:in:ok"));
        Assert.Empty(await TitlesAsync(movies, "sizeConformance:in:under"));
        Assert.Equal(["Empty", "Unruled"], await TitlesAsync(movies, "sizeConformance:unset"));
    }

    /// <summary>
    /// The verdict is written by the import, which depends on the file-facts
    /// triggers having already run on the same write.
    /// </summary>
    [Fact]
    public async Task The_verdict_lands_when_the_file_arrives()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        await movies.SyncQualityRanksAsync(Ladder, CancellationToken.None);

        await ImportAsync(movies, "Skinny", "WEB 2160p", 4 * Gb);

        Assert.Equal(["Skinny"], await TitlesAsync(movies, "sizeConformance:in:under"));
    }

    /// <summary>
    /// And it follows the rule changing, which no trigger can see.
    ///
    /// <para>Editing a quality tier is not a write to any catalogue row, so the
    /// answer changes for titles nobody has touched. Without the recompute in
    /// the sink, the shelf would be right about the ladder it had when each file
    /// last changed — which is the stale-copy defect this codebase keeps
    /// paying for.</para>
    /// </summary>
    [Fact]
    public async Task Widening_the_rule_re_judges_files_nobody_has_touched()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        await movies.SyncQualityRanksAsync(Ladder, CancellationToken.None);

        await ImportAsync(movies, "Skinny", "WEB 2160p", 4 * Gb);
        Assert.Equal(["Skinny"], await TitlesAsync(movies, "sizeConformance:in:under"));

        // The same file, a rule that now allows it. Nothing about the title has
        // changed and its verdict must still move.
        await movies.SyncQualityRanksAsync(
            [new("WEB 2160p", 100, MovieMinGb: 1, MovieMaxGb: 60, EpisodeMinMb: 0, EpisodeMaxMb: 0, ScoreCeiling: 0)],
            CancellationToken.None);

        Assert.Empty(await TitlesAsync(movies, "sizeConformance:in:under"));
        Assert.Equal(["Skinny"], await TitlesAsync(movies, "sizeConformance:in:ok"));
    }

    private static Task ImportAsync(IMovieCatalogRepository movies, string title, string quality, long sizeBytes)
        => movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: 2016,
                    WantedStatus: "covered",
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: quality,
                    TargetQuality: quality,
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: true,
                    FilePath: $@"D:\Media\{title}\{title}.mkv",
                    FileSizeBytes: sizeBytes)
            ],
            CancellationToken.None);

    private static async Task<string[]> TitlesAsync(IMovieCatalogRepository movies, string condition)
    {
        var filters = CatalogueFilters.Parse(MediaKind.Movie, [condition], out var errors);
        Assert.True(errors.Count == 0, string.Join("; ", errors));

        var page = await movies.ListPageAsync(
            new CatalogueQuery(Filters: filters, Sort: CatalogueSortFields.Title, Descending: false),
            CancellationToken.None);

        return [.. page.Items.Select(item => item.Title)];
    }

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }
}
