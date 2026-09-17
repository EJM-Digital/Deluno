using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Retention;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #494 / #492: rules select titles by steps that are data, a preview lists
/// every match with its reasons, a server that cannot be asked selects nothing,
/// a condition a server cannot answer decides nothing, and a cleanup is the
/// whole deletion - Remove, an exclusion that says why, and the media server
/// told - for exactly what was previewed.
/// </summary>
public sealed class RetentionRuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T12:00:00Z");

    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly Mock<ILibrariesRepository> _libraries = new();
    private readonly Mock<IMediaServerRepository> _servers = new();
    private readonly Mock<IMediaServerClient> _jellyfin = new();
    private readonly Mock<IUnifiedExclusionRepository> _exclusions = new();
    private readonly Mock<IRetentionRepository> _runs = new();
    private readonly Mock<IRecycleBinService> _recycleBin = new();
    private readonly FakeDisk _disk = new();
    private readonly List<MovieListItem> _catalogue = [];
    private readonly MediaServerConnection _server = new("server-1", "jellyfin", "Rig lab Jellyfin", "http://127.0.0.1:8096", "key", true, "healthy", null, null, null, "WIN-RIG", "id", Now, Now);

    public RetentionRuleTests()
    {
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        _libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            library with { Id = "lib-movies", MediaType = "movies", RootPath = @"C:\Deluno\RetentionLab\Movies" },
            library with { Id = "lib-tv", MediaType = "tv", RootPath = @"C:\Deluno\RetentionLab\TV" }
        ]);
        _movies.Setup(repo => repo.StreamTrackedFilesAsync("lib-movies", It.IsAny<CancellationToken>())).Returns(() => Files());
        _movies.Setup(repo => repo.StreamTrackedFilesAsync("lib-tv", It.IsAny<CancellationToken>())).Returns(() => AsyncEnumerable.Empty<MovieTrackedFileItem>());
        // The catalogue's own filtering is tested elsewhere; here every library condition holds.
        _movies.Setup(repo => repo.ListPageAsync(It.IsAny<CatalogueQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CataloguePage<MovieListItem>(_catalogue.ToArray(), null, false, _catalogue.Count, null));
        _movies.Setup(repo => repo.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => _catalogue.FirstOrDefault(movie => movie.Id == id));
        _movies.Setup(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_server]);
        _jellyfin.SetupGet(client => client.Kind).Returns("jellyfin");
        _recycleBin.Setup(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecycleBinMoveResult(1, 1, [], []));

        _catalogue.Add(Movie("m1", "Nosferatu", 1922, "tt0013442", "653", 13_398_513));
        _catalogue.Add(Movie("m2", "His Girl Friday", 1940, "tt0032599", "3085", 13_398_513));
        _catalogue.Add(Movie("m3", "Detour", 1945, "tt0037638", "20367", 13_398_513));
    }

    [Fact]
    public async Task A_watched_rule_selects_by_path_or_id_and_says_why()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: null, playedBy: ["delunolab2"], last: Now.AddDays(-40)),
            Item("j2", "/elsewhere/His Girl Friday.mkv", tmdb: "3085"));

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.watchedBy:anyone:"), Keep("added:beyond:30")), CancellationToken.None);

        var match = Assert.Single(evaluation.Matched);
        Assert.Equal("m1", match.EntityId);
        Assert.Contains(match.Reasons, reason => reason.StartsWith("Added is more than 30 days ago", StringComparison.Ordinal));
        Assert.Contains("Watched by anyone: delunolab2", match.Reasons);
        Assert.Empty(evaluation.UnreachableServers);
    }

    [Fact]
    public async Task A_first_version_rule_means_what_it_meant()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["delunolab"], last: Now.AddDays(-200)),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["delunolab"], last: Now.AddDays(-5)));

        var steps = RetentionStepList.FromConditions(new[] { "watched:is:true", "lastPlayed:beyond:90" }.Select(RetentionFieldCatalog.Upgrade));
        var evaluation = await Evaluator().EvaluateAsync(Rule(steps.ToArray()), CancellationToken.None);

        Assert.Equal("m1", Assert.Single(evaluation.Matched).EntityId);
    }

    [Fact]
    public async Task Also_include_adds_from_the_whole_library_and_every_step_is_counted()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653"),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["owner", "ben"], audience: 4.1),
            Item("j3", Path("Detour", 1945), tmdb: "20367", playedBy: ["owner"], audience: 3.0));

        var rule = Rule(
            Keep("server.watchedBy:nobody:"),
            new RetentionStep(RetentionStep.BlockType, RetentionStep.Include, Name: "Low rated", Steps:
            [
                Keep("server.watchedBy:everyone:"),
                Keep("server.ratingAudience:max:5")
            ]));

        var evaluation = await Evaluator().EvaluateAsync(rule, CancellationToken.None);

        Assert.Equal(["m1", "m2"], evaluation.Matched.Select(item => item.EntityId).Order());
        Assert.Equal(3, evaluation.Considered);
        Assert.Equal(1, evaluation.StepCounts![0].Count);
        Assert.Equal(2, evaluation.StepCounts[1].Count);
        Assert.Equal([1, 1], evaluation.StepCounts[1].Steps!.Select(step => step.Count));
        var friday = Assert.Single(evaluation.Matched, item => item.EntityId == "m2");
        Assert.Contains("Watched by everyone: owner, ben", friday.Reasons);
    }

    [Fact]
    public async Task A_condition_the_server_cannot_answer_decides_nothing()
    {
        // Detour is not on the server: its genres cannot be known, so "not a
        // horror film" must not select it.
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", genres: ["Horror"]),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", genres: ["Comedy"]));

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.genres:notin:Horror")), CancellationToken.None);

        Assert.Equal("m2", Assert.Single(evaluation.Matched).EntityId);
        var undecided = Assert.Single(evaluation.Candidates, item => item.Outcome == RetentionItemOutcomes.Undecided);
        Assert.Equal("m3", undecided.EntityId);
        Assert.Contains("not on Rig lab Jellyfin", undecided.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_title_the_servers_do_not_hold_is_not_never_watched()
    {
        // Detour has not reached the server yet. "Never viewed" must not pick it.
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653"),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["owner"], last: Now.AddDays(-3)));

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.lastViewed:unset:")), CancellationToken.None);

        var never = Assert.Single(evaluation.Matched);
        Assert.Equal("m1", never.EntityId);
        Assert.Equal(["Last viewed: never"], never.Reasons);
        Assert.Equal("m3", Assert.Single(evaluation.Candidates, item => item.Outcome == RetentionItemOutcomes.Undecided).EntityId);
    }

    [Fact]
    public async Task The_free_space_on_the_library_drive_is_a_condition()
    {
        ServerAnswers();
        _disk.Free = 40L << 30;
        _disk.Total = 1000L << 30;

        var tight = await Evaluator().EvaluateAsync(Rule(Keep("disk.freePercent:max:3")), CancellationToken.None);
        var plenty = await Evaluator().EvaluateAsync(Rule(Keep("disk.freeGb:max:100")), CancellationToken.None);

        Assert.Empty(tight.Matched);
        Assert.Equal(3, plenty.Matched.Count());
        Assert.Equal([@"C:\Deluno\RetentionLab\Movies"], _disk.Asked.Distinct());
    }

    [Fact]
    public async Task A_season_everyone_has_finished_is_found_under_its_show()
    {
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        var show = (SeriesListItem)RuntimeHelpers.GetUninitializedObject(typeof(SeriesListItem));
        show = show with { Id = "s1", Title = "Breaking Bad", StartYear = 2008, MetadataProvider = "tmdb", MetadataProviderId = "1396" };
        _series.Setup(repo => repo.ListPageAsync(It.IsAny<CatalogueQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CataloguePage<SeriesListItem>([show], null, false, 1, null));
        _series.Setup(repo => repo.StreamTrackedFilesAsync("lib-tv", It.IsAny<CancellationToken>())).Returns(() => EpisodeFiles(
            ("e1", 1, 1), ("e2", 1, 2), ("e3", 2, 1)));
        _series.Setup(repo => repo.GetInventoryDetailAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesInventoryDetail("s1", "Breaking Bad", 2008, 2, 3, 3, []));

        var showItem = Item("js", @"C:\Deluno\RetentionLab\TV\Breaking Bad (2008)", tmdb: "1396") with { Level = MediaServerItemLevels.Show, MediaType = "tv" };
        MediaServerItem Season(string id, int number) => Item(id, "", tmdb: null) with { Level = MediaServerItemLevels.Season, MediaType = "tv", Paths = [], ShowServerItemId = "js", SeasonNumber = number };
        MediaServerItem Episode(string id, string seasonId, int season, int number, params string[] playedBy)
            => Item(id, EpisodePath(season, number), tmdb: null, playedBy: playedBy) with
            {
                Level = MediaServerItemLevels.Episode, MediaType = "tv", ShowServerItemId = "js", SeasonServerItemId = seasonId, SeasonNumber = season, EpisodeNumber = number
            };
        _jellyfin.Setup(client => client.ListItemsAsync(_server, "tv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Reported(new MediaServerLibrary(
            [
                showItem, Season("js1", 1), Season("js2", 2),
                Episode("je1", "js1", 1, 1, "owner", "ben"), Episode("je2", "js1", 1, 2, "owner", "ben"),
                Episode("je3", "js2", 2, 1, "owner")
            ], ["owner", "ben"])));

        var evaluation = await Evaluator().EvaluateAsync(
            Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, MediaType = "tv" },
            CancellationToken.None);

        var season = Assert.Single(evaluation.Matched);
        Assert.Equal("s1:s1", season.EntityId);
        Assert.Equal("Breaking Bad — Season 1", season.Title);
    }

    [Fact]
    public async Task A_server_that_cannot_be_asked_selects_nothing_and_says_which()
    {
        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Unreachable("Deluno could not reach Rig lab Jellyfin."));

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.watchedBy:nobody:")), CancellationToken.None);

        Assert.Empty(evaluation.Matched);
        Assert.Equal(3, evaluation.Candidates.Count);
        Assert.All(evaluation.Candidates, candidate => Assert.Equal(RetentionItemOutcomes.ExcludedUnreachable, candidate.Outcome));
        // Said once, naming the server once, with one full stop.
        Assert.Equal("Deluno could not reach Rig lab Jellyfin", Assert.Single(evaluation.UnreachableServers));
    }

    [Fact]
    public async Task Titles_on_a_server_that_Deluno_does_not_manage_are_listed()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653"),
            Item("home", @"D:\Home videos\Wedding.mkv", tmdb: null) with { Title = "Wedding", Year = 2019 });

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.watchedBy:nobody:")), CancellationToken.None);

        var unmanaged = Assert.Single(evaluation.Unmanaged!);
        Assert.Equal(("Rig lab Jellyfin", "Wedding", 2019), (unmanaged.Server, unmanaged.Title, unmanaged.Year));
        Assert.DoesNotContain(evaluation.Candidates, item => item.Title == "Wedding");
    }

    [Theory]
    [InlineData("", true, "has no steps")]
    [InlineData("server.watchedBy:anyone:", false, "Choose the servers this rule reads")]
    [InlineData("nonsense:is:1", true, "nonsense")]
    [InlineData("server.watchlistedBy:anyone:", true, "None of the chosen servers can tell “Watchlisted by”")]
    [InlineData("server.allEpisodesSeenBy:everyone:", true, "does not apply to movies")]
    [InlineData("server.ratingCritic:min:", true, "needs a value")]
    public void A_rule_that_cannot_be_run_safely_is_refused(string condition, bool withServer, string expected)
    {
        var steps = condition.Length == 0 ? Array.Empty<RetentionStep>() : [Keep(condition)];
        var problems = RetentionEvaluator.Validate(
            new SaveRetentionRuleRequest("Rule", "movies", true, null, withServer ? ["server-1"] : [], false, "manual", 24, RetentionLevels.Movie, steps),
            [_server]);

        Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cleanup_removes_what_was_previewed_and_still_matches_and_nothing_else()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["delunolab"], last: Now.AddDays(-40)),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["delunolab"], last: Now.AddDays(-40)));
        _jellyfin.Setup(client => client.RemoveAsync(_server, It.IsAny<MediaServerItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerRemoval>.Reported(new MediaServerRemoval("Rig lab Jellyfin was told it is gone.")));
        var rule = Rule(Keep("server.watchedBy:anyone:")) with { RemoveFromServers = true };
        var service = Service();

        var preview = await service.PreviewAsync(rule, "person", CancellationToken.None);
        Assert.Equal(2, preview.MatchedCount);

        // Between preview and cleanup, His Girl Friday drops off the server's
        // watched list and no longer matches.
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["delunolab"], last: Now.AddDays(-40)),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085"));
        UpsertMediaExclusionRequest? excluded = null;
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertMediaExclusionRequest, CancellationToken>((request, _) => excluded = request)
            .ReturnsAsync((MediaExclusionItem?)null);

        var cleanup = await service.CleanupAsync(rule, preview, null, "person", CancellationToken.None);

        Assert.Equal(1, cleanup.RemovedCount);
        var removed = Assert.Single(cleanup.Items!, item => item.Outcome == RetentionItemOutcomes.Removed);
        Assert.Equal("m1", removed.EntityId);
        Assert.Contains("Rig lab Jellyfin was told it is gone.", removed.Message, StringComparison.Ordinal);
        Assert.Equal(RetentionItemOutcomes.Changed, Assert.Single(cleanup.Items!, item => item.EntityId == "m2").Outcome);

        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
        _movies.Verify(repo => repo.DeleteAsync("m2", It.IsAny<CancellationToken>()), Times.Never);
        _recycleBin.Verify(bin => bin.MoveAsync(It.Is<IReadOnlyList<TrackedLibraryFile>>(files => files.Any(file => file.FilePath.EndsWith("Nosferatu (1922).mkv", StringComparison.Ordinal))), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(excluded);
        Assert.Equal(MediaExclusionSourceKinds.Retention, excluded!.SourceKind);
        Assert.Equal("rule-1", excluded.SourceId);
        Assert.Equal("imdb:tt0013442", excluded.EntryKey);
        Assert.Equal("tmdb:653", excluded.Provider);
        Assert.Null(excluded.DurationDays);
        Assert.StartsWith("Cleaned up by retention rule “Watched films” on 17 Sep 2026", excluded.Reason, StringComparison.Ordinal);
        _jellyfin.Verify(client => client.RemoveAsync(_server, It.Is<MediaServerItem>(item => item.ServerItemId == "j1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_cleanup_while_a_server_cannot_be_asked_removes_nothing()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["delunolab"], last: Now.AddDays(-40)));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        var service = Service();
        var preview = await service.PreviewAsync(rule, "person", CancellationToken.None);
        Assert.Equal(1, preview.MatchedCount);

        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Unreachable("Deluno could not reach Rig lab Jellyfin."));

        var cleanup = await service.CleanupAsync(rule, preview, null, "person", CancellationToken.None);

        Assert.Equal(0, cleanup.RemovedCount);
        Assert.StartsWith("Nothing was removed", cleanup.Summary, StringComparison.Ordinal);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private RetentionEvaluator Evaluator()
        => new(
            new RetentionUnitReader(_movies.Object, _series.Object, _libraries.Object, new FixedClock(Now)),
            _servers.Object,
            new MediaServerClientRegistry([_jellyfin.Object]),
            _disk,
            new FixedClock(Now));

    private RetentionService Service()
    {
        var intake = new Mock<IIntakeRepository>();
        var removal = new TitleRemovalService(_libraries.Object, intake.Object, Mock.Of<IJobQueueRepository>(), Mock.Of<IActivityFeedRepository>(), _recycleBin.Object);
        return new RetentionService(_runs.Object, Evaluator(), removal, _movies.Object, _series.Object, _exclusions.Object,
            new MediaServerClientRegistry([_jellyfin.Object]), Mock.Of<IActivityFeedRepository>(), new FixedClock(Now), NullLogger<RetentionService>.Instance);
    }

    private static RetentionStep Keep(string condition) => RetentionStep.KeepOnly(condition);

    private static RetentionRule Rule(params RetentionStep[] steps)
        => new("rule-1", "Watched films", "movies", true, ["server-1"], false, "manual", 24, null, Now, Now, RetentionLevels.Movie, steps);

    private void ServerAnswers(params MediaServerItem[] items)
        => _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Reported(new MediaServerLibrary(items, ["owner", "ben"])));

    private static string Path(string title, int year) => $@"C:\Deluno\RetentionLab\Movies\{title} ({year})\{title} ({year}).mkv";

    private static string EpisodePath(int season, int episode) => $@"C:\Deluno\RetentionLab\TV\Breaking Bad (2008)\Season {season}\Breaking Bad - S{season:00}E{episode:00}.mkv";

    private static MediaServerItem Item(
        string id,
        string path,
        string? tmdb,
        string[]? playedBy = null,
        DateTimeOffset? last = null,
        double? audience = null,
        string[]? genres = null)
        => new MediaServerItem(id, "movies", "x", null, tmdb, null, null, path.Length == 0 ? [] : [path], null, new MediaServerWatchState((playedBy ?? []).Length > 0, playedBy ?? [], last, true))
        {
            Viewers = (playedBy ?? []).Select(user => new MediaServerViewer(user, true, 1, last, false, false)).ToArray(),
            Ratings = new MediaServerRatings(Audience: audience),
            Genres = genres ?? []
        };

    private MovieListItem Movie(string id, string title, int year, string imdb, string tmdb, long size)
    {
        var movie = (MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem));
        return movie with { Id = id, Title = title, ReleaseYear = year, ImdbId = imdb, MetadataProvider = "tmdb", MetadataProviderId = tmdb, FileSizeBytes = size };
    }

    private async IAsyncEnumerable<MovieTrackedFileItem> Files()
    {
        foreach (var movie in _catalogue)
        {
            var tracked = (MovieTrackedFileItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieTrackedFileItem));
            yield return tracked with { MovieId = movie.Id, LibraryId = "lib-movies", FilePath = Path(movie.Title, movie.ReleaseYear!.Value) };
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<SeriesTrackedFileItem> EpisodeFiles(params (string Id, int Season, int Episode)[] episodes)
    {
        foreach (var (id, season, episode) in episodes)
        {
            yield return new SeriesTrackedFileItem("s1", id, "lib-tv", "Breaking Bad", 2008, season, episode, EpisodePath(season, episode), 1_000_000, Now.AddDays(-100), Now);
        }

        await Task.CompletedTask;
    }

    private sealed class FakeDisk : IRetentionDiskSpace
    {
        public long Free { get; set; } = 500L << 30;

        public long Total { get; set; } = 1000L << 30;

        public List<string> Asked { get; } = [];

        public (long FreeBytes, long TotalBytes)? Read(string path)
        {
            Asked.Add(path);
            return (Free, Total);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
