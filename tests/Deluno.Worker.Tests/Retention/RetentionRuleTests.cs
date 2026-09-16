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
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #494 / #492: rules select titles by conditions that are data, a preview
/// lists every match with its reasons, a server that cannot be asked selects
/// nothing, and a cleanup is the whole deletion - Remove, an exclusion that
/// says why, and the media server told - for exactly what was previewed.
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
    private readonly List<MovieListItem> _catalogue = [];
    private readonly MediaServerConnection _server = new("server-1", "jellyfin", "Rig lab Jellyfin", "http://127.0.0.1:8096", "key", true, "healthy", null, null, null, "WIN-RIG", "id", Now, Now);

    public RetentionRuleTests()
    {
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        _libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library with { Id = "lib-movies", MediaType = "movies" }]);
        _movies.Setup(repo => repo.StreamTrackedFilesAsync("lib-movies", It.IsAny<CancellationToken>())).Returns(() => Files());
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
    }

    [Fact]
    public async Task A_watched_rule_selects_by_path_or_id_and_says_why()
    {
        ServerAnswers(
            Item("j1", @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv", tmdb: null, played: true, playedBy: ["delunolab2"], last: Now.AddDays(-40)),
            Item("j2", "/elsewhere/His Girl Friday.mkv", tmdb: "3085", played: false));

        var evaluation = await Evaluator().EvaluateAsync(Rule(["watched:is:true", "added:beyond:30"]), CancellationToken.None);

        var match = Assert.Single(evaluation.Matched);
        Assert.Equal("m1", match.EntityId);
        Assert.Contains(match.Reasons, reason => reason.StartsWith("Added is more than 30 days ago", StringComparison.Ordinal));
        Assert.Contains(match.Reasons, reason => reason == "Watched: watched by delunolab2 on Rig lab Jellyfin");
        Assert.Empty(evaluation.UnreachableServers);
    }

    [Fact]
    public async Task A_server_that_cannot_be_asked_selects_nothing_and_says_which()
    {
        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable("Deluno could not reach Rig lab Jellyfin."));

        var evaluation = await Evaluator().EvaluateAsync(Rule(["watched:is:false"]), CancellationToken.None);

        Assert.Empty(evaluation.Matched);
        Assert.Equal(2, evaluation.Candidates.Count);
        Assert.All(evaluation.Candidates, candidate => Assert.Equal(RetentionItemOutcomes.ExcludedUnreachable, candidate.Outcome));
        // Said once, naming the server once, with one full stop.
        Assert.Equal("Deluno could not reach Rig lab Jellyfin", Assert.Single(evaluation.UnreachableServers));
    }

    [Fact]
    public async Task Last_played_counts_the_most_recent_play_on_any_server()
    {
        ServerAnswers(
            Item("j1", @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv", tmdb: "653", played: true, last: Now.AddDays(-200)),
            Item("j2", @"C:\Deluno\RetentionLab\Movies\His Girl Friday (1940)\His Girl Friday (1940).mkv", tmdb: "3085", played: true, last: Now.AddDays(-5)));

        var evaluation = await Evaluator().EvaluateAsync(Rule(["lastPlayed:beyond:90"]), CancellationToken.None);

        Assert.Equal("m1", Assert.Single(evaluation.Matched).EntityId);
    }

    [Theory]
    [InlineData(new string[0], true, "Add at least one condition")]
    [InlineData(new[] { "watched:is:true" }, false, "Choose the media servers whose watch history")]
    [InlineData(new[] { "nonsense:is:1" }, true, "nonsense")]
    public void A_rule_that_cannot_be_run_safely_is_refused(string[] conditions, bool withServer, string expected)
    {
        var problems = RetentionEvaluator.Validate(
            new SaveRetentionRuleRequest("Rule", "movies", true, conditions, withServer ? ["server-1"] : [], false, "manual", 24),
            [_server]);

        Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cleanup_removes_what_was_previewed_and_still_matches_and_nothing_else()
    {
        ServerAnswers(
            Item("j1", @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv", tmdb: "653", played: true, playedBy: ["delunolab"], last: Now.AddDays(-40)),
            Item("j2", @"C:\Deluno\RetentionLab\Movies\His Girl Friday (1940)\His Girl Friday (1940).mkv", tmdb: "3085", played: true, playedBy: ["delunolab"], last: Now.AddDays(-40)));
        _jellyfin.Setup(client => client.RemoveAsync(_server, It.IsAny<MediaServerItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerRemoval>.Reported(new MediaServerRemoval("Rig lab Jellyfin was told it is gone.")));
        var rule = Rule(["watched:is:true"], removeFromServers: true);
        var service = Service();

        var preview = await service.PreviewAsync(rule, "person", CancellationToken.None);
        Assert.Equal(2, preview.MatchedCount);

        // Between preview and cleanup, His Girl Friday is played again by nobody -
        // it drops off the server's watched list and no longer matches.
        ServerAnswers(
            Item("j1", @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv", tmdb: "653", played: true, playedBy: ["delunolab"], last: Now.AddDays(-40)),
            Item("j2", @"C:\Deluno\RetentionLab\Movies\His Girl Friday (1940)\His Girl Friday (1940).mkv", tmdb: "3085", played: false));
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
        ServerAnswers(Item("j1", @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv", tmdb: "653", played: true, last: Now.AddDays(-40)));
        var rule = Rule(["watched:is:true"]);
        var service = Service();
        var preview = await service.PreviewAsync(rule, "person", CancellationToken.None);
        Assert.Equal(1, preview.MatchedCount);

        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable("Deluno could not reach Rig lab Jellyfin."));

        var cleanup = await service.CleanupAsync(rule, preview, null, "person", CancellationToken.None);

        Assert.Equal(0, cleanup.RemovedCount);
        Assert.StartsWith("Nothing was removed", cleanup.Summary, StringComparison.Ordinal);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private RetentionEvaluator Evaluator()
        => new(_movies.Object, _series.Object, _libraries.Object, _servers.Object, new MediaServerClientRegistry([_jellyfin.Object]), new FixedClock(Now));

    private RetentionService Service()
    {
        var intake = new Mock<IIntakeRepository>();
        var removal = new TitleRemovalService(_libraries.Object, intake.Object, Mock.Of<IJobQueueRepository>(), Mock.Of<IActivityFeedRepository>(), _recycleBin.Object);
        return new RetentionService(_runs.Object, Evaluator(), removal, _movies.Object, _series.Object, _exclusions.Object,
            new MediaServerClientRegistry([_jellyfin.Object]), Mock.Of<IActivityFeedRepository>(), new FixedClock(Now), NullLogger<RetentionService>.Instance);
    }

    private RetentionRule Rule(IReadOnlyList<string> conditions, bool removeFromServers = false)
        => new("rule-1", "Watched films", "movies", true, conditions, ["server-1"], removeFromServers, "manual", 24, null, Now, Now);

    private void ServerAnswers(params MediaServerItem[] items)
        => _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Reported(items));

    private static MediaServerItem Item(string id, string path, string? tmdb, bool played, string[]? playedBy = null, DateTimeOffset? last = null)
        => new(id, "movies", "x", null, tmdb, null, null, [path], null, new MediaServerWatchState(played, playedBy ?? [], last, true));

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
            yield return tracked with { MovieId = movie.Id, LibraryId = "lib-movies", FilePath = $@"C:\Deluno\RetentionLab\Movies\{movie.Title} ({movie.ReleaseYear})\{movie.Title} ({movie.ReleaseYear}).mkv" };
        }

        await Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
