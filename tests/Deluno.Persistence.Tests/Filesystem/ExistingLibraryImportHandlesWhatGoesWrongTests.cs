using System.Reflection;
using System.Runtime.ExceptionServices;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The existing-library import (coverage register, area engines) when something is not as it should be: a loose TV file with no episode, a slice that
/// runs out of time, one title the catalogue will not take, a slice that fails, and the title read from a path.
/// Everything runs over a real library folder and the real SQLite catalogues; the only thing stood in for is a catalogue that refuses on purpose.
/// </summary>
public sealed class ExistingLibraryImportHandlesWhatGoesWrongTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T08:00:00Z");

    /// <summary>A catalogue that does everything the real one does, and refuses where the test says so.</summary>
    private class Refusing<T> : DispatchProxy where T : class
    {
        public T Inner { get; set; } = default!;

        public Func<string, object?[], Exception?> Refuse { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Refuse(targetMethod!.Name, args ?? []) is { } refusal)
            {
                throw refusal;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
                throw;
            }
        }
    }

    private static T Refusing_<T>(T inner, Func<string, object?[], Exception?> refuse) where T : class
    {
        var proxy = DispatchProxy.Create<T, Refusing<T>>();
        var forwarding = (Refusing<T>)(object)proxy;
        forwarding.Inner = inner;
        forwarding.Refuse = refuse;
        return proxy;
    }

    private sealed record World(
        TestStorage Storage,
        SqliteLibrariesRepository Libraries,
        SqliteLibraryImportRunsRepository Runs,
        SqliteMovieCatalogRepository Movies,
        string Root,
        string LibraryId) : IDisposable
    {
        public void Dispose() => Storage.Dispose();
    }

    private static async Task<World> WorldAsync(string mediaType, Action<string>? lay = null)
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var root = Directory.CreateDirectory(Path.Combine(storage.DataRoot, mediaType == "movies" ? "Films" : "Shows")).FullName;
        lay?.Invoke(root);
        var libraries = new SqliteLibrariesRepository(storage.Factory, time);
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: mediaType == "movies" ? "Films" : "Shows", MediaType: mediaType, Purpose: "Main", RootPath: root,
                DownloadsPath: Path.Combine(storage.DataRoot, "downloads"), QualityProfileId: null, ImportWorkflow: "standard",
                ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: null, ProcessorFailureMode: null,
                AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: 25),
            CancellationToken.None);
        return new World(storage, libraries, new SqliteLibraryImportRunsRepository(storage.Factory, time), new SqliteMovieCatalogRepository(storage.Factory, time), root, library.Id);
    }

    private static ExistingLibraryImportService ServiceFor(World world, IMovieCatalogRepository? movies = null, LibraryImportSliceOptions? slice = null)
    {
        var time = new FixedTimeProvider(Now);
        return new(
            world.Libraries,
            world.Runs,
            movies ?? world.Movies,
            new SqliteSeriesCatalogRepository(world.Storage.Factory, time),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            new QualityModelService(world.Storage.Factory, time),
            time,
            NullLogger<ExistingLibraryImportService>.Instance,
            slice);
    }

    private static void Film(string root, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(folder, name + ".1080p.BluRay.mkv"), string.Empty);
    }

    [Fact]
    public async Task A_loose_video_file_at_the_root_of_a_TV_library_with_no_season_or_episode_is_set_aside_and_cannot_be_imported()
    {
        using var world = await WorldAsync("tv", root => File.WriteAllText(Path.Combine(root, "Some.Show.Finale.1080p.WEB.mkv"), string.Empty));

        var preview = await ServiceFor(world).PreviewAsync(world.LibraryId, cursor: null, take: 10, CancellationToken.None);

        var loose = Assert.Single(preview!.Items);
        Assert.False(loose.CanImport);
        Assert.Equal("ambiguousEpisode", loose.IssueKind);
        Assert.Equal("The file name does not say which season and episode this is.", loose.IssueDetail);
    }

    [Fact]
    public async Task A_slice_hands_back_when_its_time_is_up_before_it_has_examined_anything_and_the_default_is_twenty_seconds()
    {
        using var world = await WorldAsync("movies", root => { Film(root, "Silent Harbour (1991)"); Film(root, "Quiet Coast (2004)"); });
        var service = ServiceFor(world, slice: LibraryImportSliceOptions.Default with { MaxSliceDuration = TimeSpan.Zero });
        var started = await service.StartAsync(world.LibraryId, CancellationToken.None);

        var outcome = await service.RunSliceAsync(started!.Run.Id, CancellationToken.None);

        Assert.True(outcome.MoreWorkRemains, "a slice that ran out of time hands back for another");
        Assert.Equal(0, outcome.ProcessedInSlice);
        Assert.Empty(await world.Movies.ListAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(20), LibraryImportSliceOptions.Default.MaxSliceDuration);
        Assert.True(LibraryImportSliceOptions.Default.MaxSliceDuration < TimeSpan.FromMinutes(2), "well inside the two-minute lease of the job that runs it");
    }

    [Fact]
    public async Task One_title_the_catalogue_refuses_does_not_lose_the_rest_of_its_batch_it_is_recorded_as_a_write_failure_and_the_run_completes()
    {
        using var world = await WorldAsync("movies", root => { Film(root, "Good Film One (1991)"); Film(root, "Bad Film (2001)"); Film(root, "Good Film Two (2004)"); });
        var movies = Refusing_<IMovieCatalogRepository>(world.Movies, (name, args) =>
            name == nameof(IMovieCatalogRepository.ImportExistingBatchAsync) && args.OfType<System.Collections.IEnumerable>().SelectMany(list => list.Cast<object>()).Any(item => item.ToString()!.Contains("Bad Film", StringComparison.Ordinal))
                ? new InvalidOperationException("The catalogue would not take this title.")
                : null);
        var service = ServiceFor(world, movies);
        var started = await service.StartAsync(world.LibraryId, CancellationToken.None);

        var outcome = await service.RunSliceAsync(started!.Run.Id, CancellationToken.None);

        Assert.Equal("completed", outcome.RunStatus);
        Assert.Equal(["Good Film One", "Good Film Two"], (await world.Movies.ListAsync(CancellationToken.None)).Select(movie => movie.Title).Order().ToArray());
        var issue = Assert.Single(await service.ListIssuesAsync(world.LibraryId, 10, CancellationToken.None));
        Assert.Equal("writeFailed", issue.Kind);
        Assert.Contains("Bad Film", issue.SourcePath);
        Assert.Equal("The catalogue would not take this title.", issue.Detail);
    }

    [Fact]
    public async Task A_slice_that_fails_records_why_on_the_run_and_throws_so_the_job_system_retries()
    {
        using var world = await WorldAsync("movies", root => Film(root, "Silent Harbour (1991)"));
        var movies = Refusing_<IMovieCatalogRepository>(world.Movies, (name, _) =>
            name == nameof(IMovieCatalogRepository.ListRecordedFileQualitiesAsync) ? new InvalidOperationException("the catalogue is locked") : null);
        var service = ServiceFor(world, movies);
        var started = await service.StartAsync(world.LibraryId, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunSliceAsync(started!.Run.Id, CancellationToken.None));

        Assert.Equal("the catalogue is locked", failure.Message);
        var run = (await service.GetProgressAsync(world.LibraryId, CancellationToken.None))!.Run;
        Assert.Equal("the catalogue is locked", run.LastError);
        Assert.True(LibraryImportRunStatuses.IsActive(run.Status), "the run keeps its place so the retry carries on from it");
    }

    [Fact]
    public async Task The_title_guess_of_a_path_is_read_from_its_first_segment_under_the_library_root_and_a_path_outside_the_root_is_refused()
    {
        using var world = await WorldAsync("movies");
        var service = ServiceFor(world);

        var folder = await service.ReadTitleOfPathAsync(world.LibraryId, Path.Combine(world.Root, "Silent Harbour (1991)", "Extras", "Behind.The.Scenes.mkv"), CancellationToken.None);
        var file = await service.ReadTitleOfPathAsync(world.LibraryId, Path.Combine(world.Root, "Quiet.Coast.2004.1080p.mkv"), CancellationToken.None);
        var outside = await service.ReadTitleOfPathAsync(world.LibraryId, Path.Combine(Path.GetDirectoryName(world.Root)!, "Elsewhere", "Other Film (2010)", "x.mkv"), CancellationToken.None);
        var climbing = await service.ReadTitleOfPathAsync(world.LibraryId, Path.Combine(world.Root, "..", "Elsewhere", "Other Film (2010)"), CancellationToken.None);
        var unknownLibrary = await service.ReadTitleOfPathAsync("no-such-library", Path.Combine(world.Root, "Silent Harbour (1991)"), CancellationToken.None);

        Assert.Equal(new TitleNameGuess("Silent Harbour", 1991), folder);
        Assert.Equal("Quiet Coast", file!.Title);
        Assert.Equal(2004, file.Year);
        Assert.Null(outside);
        Assert.Null(climbing);
        Assert.Null(unknownLibrary);
    }
}
