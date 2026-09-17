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
using Deluno.Notifications;
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
    private readonly InMemoryRetentionRepository _store = new();
    private readonly Mock<IOutboundNotificationService> _notifications = new();
    private readonly MutableClock _clock = new(Now);
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
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["james", "ben"], audience: 4.1),
            Item("j3", Path("Detour", 1945), tmdb: "20367", playedBy: ["james"], audience: 3.0));

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
        Assert.Contains("Watched by everyone: james, ben", friday.Reasons);
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
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["james"], last: Now.AddDays(-3)));

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
        SeasonLibrary();

        var evaluation = await Evaluator().EvaluateAsync(
            Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season },
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
            new SaveRetentionRuleRequest("Rule", RetentionLevels.Movie, true, steps, withServer ? ["server-1"] : [], false),
            [_server]);

        Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_match_is_marked_leaving_soon_and_nothing_is_removed_before_its_day()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085"));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        var mark = Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
        Assert.Equal(("m1", RetentionMarkStates.Leaving, Now.AddDays(14)), (mark.EntityId, mark.State, mark.RemoveAfterUtc));
        Assert.Equal(["Watched by anyone: james"], mark.Reasons);
        Assert.Equal(1, run.MatchedCount);
        Assert.Equal(RetentionItemOutcomes.Marked, Assert.Single(run.Items!).Outcome);
        // Said once: the total adds nothing when it is what was just marked.
        Assert.Equal("1 title marked leaving soon.", run.Summary);
        Assert.Equal("Nothing changed; 1 title still leaving soon.", (await Service().CheckAsync(rule, "schedule", CancellationToken.None)).Summary);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifications.Verify(service => service.DispatchAsync(RetentionService.LeavingSoonEvent, "1 title leaving soon", It.Is<string>(text => text.Contains("Nosferatu on 1 Oct 2026")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task On_its_day_a_title_that_still_matches_goes_and_one_that_stopped_matching_does_not()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["james"], last: Now.AddDays(-40)));
        _jellyfin.Setup(client => client.RemoveAsync(_server, It.IsAny<MediaServerItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerRemoval>.Reported(new MediaServerRemoval("told")));
        var rule = Rule(Keep("server.watchedBy:anyone:"), Keep("server.lastViewed:beyond:30")) with { RemoveFromServers = true };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal(2, (await _store.ListMarksAsync(rule.Id, CancellationToken.None)).Count);

        // A fortnight on, somebody has watched His Girl Friday again.
        _clock.Now = Now.AddDays(15);
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["james"], last: _clock.Now.AddDays(-2)));
        UpsertMediaExclusionRequest? excluded = null;
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertMediaExclusionRequest, CancellationToken>((request, _) => excluded = request)
            .ReturnsAsync((MediaExclusionItem?)null);

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(1, run.RemovedCount);
        Assert.Equal("m1", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
        Assert.Equal("No longer matches the rule.", Assert.Single(run.Items!, item => item.EntityId == "m2").Message);
        Assert.Empty(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
        _movies.Verify(repo => repo.DeleteAsync("m2", It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(excluded);
        Assert.Equal(MediaExclusionSourceKinds.Retention, excluded!.SourceKind);
        Assert.Equal("imdb:tt0013442", excluded.EntryKey);
        Assert.StartsWith("Removed by retention rule “Watched films” on 2 Oct 2026", excluded.Reason, StringComparison.Ordinal);
        _jellyfin.Verify(client => client.RemoveAsync(_server, It.Is<MediaServerItem>(item => item.ServerItemId == "j1"), It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(service => service.DispatchAsync(RetentionService.RemovedEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_rule_that_asks_first_waits_for_a_person_and_then_removes()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        var rule = Rule(Keep("server.watchedBy:anyone:")) with { RemovalMode = RetentionRemovalModes.Ask, WaitDays = 1 };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _clock.Now = Now.AddDays(2);
        var check = await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal(RetentionMarkStates.Ready, Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).State);
        Assert.Equal(0, check.RemovedCount);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifications.Verify(service => service.DispatchAsync(RetentionService.WaitingEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        var confirmed = await Service().ConfirmAsync(rule, ["m1"], CancellationToken.None);

        Assert.Equal(1, confirmed.RemovedCount);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_kept_title_leaves_leaving_soon_and_is_not_marked_again()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        var kept = await Service().KeepAsync(new SaveRetentionKeepRequest("m1", null, null, "Kids' favourite"), CancellationToken.None);
        _clock.Now = Now.AddDays(20);
        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal((1, "Nosferatu", null, "Kids' favourite"), (kept!.ClearedMarks, kept.Keep.Title, kept.Keep.RuleId, kept.Keep.Note));
        Assert.Empty(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
        Assert.Empty(run.Items!);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_check_while_a_server_cannot_be_asked_changes_nothing_and_holds_what_is_due()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _clock.Now = Now.AddDays(30);
        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Unreachable("Deluno could not reach Rig lab Jellyfin."));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        var mark = Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
        Assert.Equal((RetentionMarkStates.Held, "Deluno could not reach Rig lab Jellyfin"), (mark.State, mark.StateReason));
        Assert.Equal(0, run.RemovedCount);
        Assert.StartsWith("Nothing changed: Deluno could not reach Rig lab Jellyfin.", run.Summary, StringComparison.Ordinal);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_season_removed_by_a_rule_loses_its_files_and_is_no_longer_wanted_until_undone()
    {
        SeasonLibrary();
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1 };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _clock.Now = Now.AddDays(2);
        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("s1:s1", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
        _recycleBin.Verify(bin => bin.MoveAsync(It.Is<IReadOnlyList<TrackedLibraryFile>>(files => files.Count == 2 && files.All(file => file.FilePath.Contains("Season 1"))), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Once);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.Order().SequenceEqual(new[] { "e1", "e2" })), false, It.IsAny<CancellationToken>()), Times.Once);
        _series.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var removals = await _store.ListEpisodeRemovalsAsync("s1", CancellationToken.None);
        Assert.Equal(["e1", "e2"], removals.Select(item => item.EpisodeId).Order());
        Assert.All(removals, item => Assert.StartsWith("Removed by retention rule “Watched films”", item.Reason, StringComparison.Ordinal));

        var undone = await Service().UndoEpisodeRemovalAsync("e1", CancellationToken.None);

        Assert.NotNull(undone);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "e1" })), true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("e2", Assert.Single(await _store.ListEpisodeRemovalsAsync("s1", CancellationToken.None)).EpisodeId);
    }

    private RetentionEvaluator Evaluator()
        => new(
            new RetentionUnitReader(_movies.Object, _series.Object, _libraries.Object, _clock),
            _servers.Object,
            new MediaServerClientRegistry([_jellyfin.Object]),
            _disk,
            _clock);

    private RetentionService Service()
    {
        var intake = new Mock<IIntakeRepository>();
        var removal = new TitleRemovalService(_libraries.Object, intake.Object, Mock.Of<IJobQueueRepository>(), Mock.Of<IActivityFeedRepository>(), _recycleBin.Object);
        return new RetentionService(_store, Evaluator(), removal, _movies.Object, _series.Object, _libraries.Object, _recycleBin.Object, _exclusions.Object,
            new MediaServerClientRegistry([_jellyfin.Object]), Mock.Of<IActivityFeedRepository>(), _notifications.Object, _clock, NullLogger<RetentionService>.Instance);
    }

    /// <summary>Breaking Bad: season 1 watched by everyone, season 2 by one person.</summary>
    private void SeasonLibrary()
    {
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
                Episode("je1", "js1", 1, 1, "james", "ben"), Episode("je2", "js1", 1, 2, "james", "ben"),
                Episode("je3", "js2", 2, 1, "james")
            ], ["james", "ben"])));

    }

    private static RetentionStep Keep(string condition) => RetentionStep.KeepOnly(condition);

    private static RetentionRule Rule(params RetentionStep[] steps)
        => new("rule-1", "Watched films", RetentionLevels.Movie, true, steps, ["server-1"], false, 14, RetentionRemovalModes.Automatic, 8, null, Now, Now);

    private void ServerAnswers(params MediaServerItem[] items)
        => _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Reported(new MediaServerLibrary(items, ["james", "ben"])));

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

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
