using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Platform;
using Deluno.Quality;
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
    private readonly Mock<INotificationService> _notificationPreferences = new();
    private NotificationPreferences _preferences = new();
    private readonly MutableClock _clock = new(Now);
    private readonly Mock<IRecycleBinService> _recycleBin = new();
    private readonly FakeDisk _disk = new();
    private readonly FakeRetentionSeerr _seerr = new();
    private readonly Mock<IQualityModelService> _qualityModel = new();
    private readonly List<MovieListItem> _catalogue = [];
    private readonly Mock<IMediaServerClient> _other = new();
    private readonly Mock<IMediaServerPlaybackReader> _otherPlayback;
    private readonly Mock<IMediaServerPlaybackReader> _playback;
    private MediaServerAnswer<IReadOnlyList<MediaServerPlay>> _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([]);
    private readonly MediaServerConnection _server =new("server-1", "jellyfin", "Rig lab Jellyfin", "http://127.0.0.1:8096", "key", true, "healthy", null, null, null, "WIN-RIG", "id", Now, Now);

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
        _notificationPreferences.Setup(service => service.GetPreferencesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _preferences);
        _qualityModel.Setup(service => service.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QualityModelSnapshot(
            "quality-model/v2",
            [
                new QualityTierDefinition("SD", 1, 0, QualityTierDefinition.DefaultPreferred(0, 2), 2, 0, QualityTierDefinition.DefaultPreferred(0, 400), 400, 100),
                new QualityTierDefinition("HD-1080p", 5, 4, QualityTierDefinition.DefaultPreferred(4, 12), 12, 800, QualityTierDefinition.DefaultPreferred(800, 3000), 3000, 100)
            ],
            new QualityUpgradeStopPolicy(true, false),
            Now));
        _jellyfin.SetupGet(client => client.Kind).Returns("jellyfin");
        // Jellyfin can say what is being played (#1012); each test sets what it says.
        _other.SetupGet(client => client.Kind).Returns("emby");
        _otherPlayback = _other.As<IMediaServerPlaybackReader>();
        _playback = _jellyfin.As<IMediaServerPlaybackReader>();
        _playback.Setup(reader => reader.ListPlayingAsync(_server, It.IsAny<CancellationToken>())).ReturnsAsync(() => _playing);
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
        Assert.Equal(["Last viewed (finished): never"], never.Reasons);
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
        // What it matched on each server, for the Leaving soon collections (#521).
        Assert.Equal([new RetentionServerItem("server-1", "j1", MediaServerItemLevels.Movie, null)], mark.ServerItems);
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
        _notifications.Verify(service => service.DispatchAsync(RetentionService.WaitingConfirmationEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(service => service.DispatchAsync(RetentionService.WaitingUnreachableEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var confirmed = await Service().ConfirmAsync(rule, ["m1"], CancellationToken.None);

        Assert.Equal(1, confirmed.RemovedCount);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_unreachable_server_holding_a_due_removal_waits_with_its_own_event_not_the_confirmation_ones()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _clock.Now = Now.AddDays(30);
        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Unreachable("Deluno could not reach Rig lab Jellyfin."));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(0, run.RemovedCount);
        _notifications.Verify(service => service.DispatchAsync(RetentionService.WaitingUnreachableEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(service => service.DispatchAsync(RetentionService.WaitingConfirmationEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

    /// <summary>#856: retention deleting an episode's files fires the same
    /// "file.deleted" outbound event a manual delete does, gated by the same
    /// toggle.</summary>
    [Fact]
    public async Task A_season_removed_by_a_rule_dispatches_file_deleted_when_the_toggle_is_on()
    {
        _preferences = new NotificationPreferences { FileDeletedEnabled = true };
        SeasonLibrary();
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1 };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _clock.Now = Now.AddDays(2);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _notifications.Verify(service => service.DispatchAsync("file.deleted", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_season_removed_by_a_rule_stays_silent_when_the_file_deleted_toggle_is_off()
    {
        _preferences = new NotificationPreferences { FileDeletedEnabled = false };
        SeasonLibrary();
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1 };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _clock.Now = Now.AddDays(2);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _notifications.Verify(service => service.DispatchAsync("file.deleted", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// #931: "removed outside Deluno" (and the unmonitor that comes with it)
    /// is FilesystemReconciliationService's own rule for a file the reconciliation
    /// scan finds gone with no explanation. Retention unmonitors on purpose,
    /// for its own reason ("Removed by retention rule ..."), by calling
    /// MarkTrackedFileMissingAsync and UpdateEpisodeMonitoredAsync directly -
    /// the same primitives, but never through the reconciliation repair path -
    /// so it must never also write the reconciliation's own sentence.
    /// </summary>
    [Fact]
    public async Task A_season_removed_by_a_rule_never_writes_the_removed_outside_Deluno_line()
    {
        SeasonLibrary();
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1 };
        _store.Add(rule);
        var activity = new Mock<IActivityFeedRepository>();
        RetentionService ServiceWithCapturedActivity()
        {
            var intake = new Mock<IIntakeRepository>();
            var removal = new TitleRemovalService(_libraries.Object, intake.Object, Mock.Of<IJobQueueRepository>(), activity.Object, _recycleBin.Object);
            return new RetentionService(_store, Evaluator(), removal, _movies.Object, _series.Object, _libraries.Object, _recycleBin.Object, _exclusions.Object,
                new MediaServerClientRegistry([_jellyfin.Object]), _seerr, activity.Object, _notifications.Object, _clock, NullLogger<RetentionService>.Instance, _notificationPreferences.Object);
        }

        await ServiceWithCapturedActivity().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        var run = await ServiceWithCapturedActivity().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("s1:s1", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.Order().SequenceEqual(new[] { "e1", "e2" })), false, It.IsAny<CancellationToken>()), Times.Once);
        activity.Verify(
            feed => feed.RecordActivityAsync(
                It.IsAny<string>(),
                It.Is<string>(message => message.Contains("removed outside Deluno", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ Seerr (#522)

    [Fact]
    public async Task Request_fields_read_who_asked_when_and_how_often()
    {
        _seerr.Library = new SeerrLibrary(
        [
            new SeerrRequest(11, "movies", "653", null, "ben", Now.AddDays(-60), Now.AddDays(-59)),
            new SeerrRequest(11, "movies", "653", null, "james", Now.AddDays(-20), null),
            new SeerrRequest(12, "movies", "3085", null, "ben", Now.AddDays(-5), Now.AddDays(-5))
        ], ["ben", "james", "sam"]);
        ServerAnswers();

        var requestedByBen = await Evaluator().EvaluateAsync(Rule(Keep("seerr.requestedBy:in:ben"), Keep("seerr.approvalDate:beyond:30")), CancellationToken.None);
        var match = Assert.Single(requestedByBen.Matched);
        Assert.Equal("m1", match.EntityId);
        Assert.Equal("Requested by includes any of ben: ben, james", match.Reasons[0]);

        var neverRequested = await Evaluator().EvaluateAsync(Rule(Keep("seerr.isRequested:is:false")), CancellationToken.None);
        Assert.Equal(["m3"], neverRequested.Matched.Select(item => item.EntityId));

        var twice = await Evaluator().EvaluateAsync(Rule(Keep("seerr.timesRequested:min:2")), CancellationToken.None);
        Assert.Equal(["m1"], twice.Matched.Select(item => item.EntityId));
    }

    [Fact]
    public async Task A_Seerr_that_cannot_be_read_decides_nothing_it_would_have_decided()
    {
        _seerr.Library = new SeerrLibrary([], []);
        _seerr.Unreachable = "Deluno could not reach Lab Seerr.";
        ServerAnswers();
        var rule = Rule(Keep("seerr.isRequested:is:false"));
        _store.Add(rule);

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.StartsWith("Nothing changed: Deluno could not reach Lab Seerr", run.Summary, StringComparison.Ordinal);
        Assert.Empty(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
    }

    [Fact]
    public void A_rule_reading_requests_needs_a_Seerr_server()
    {
        var problems = RetentionEvaluator.Validate(
            new SaveRetentionRuleRequest("Rule", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("seerr.isRequested:is:true")], [], false),
            [_server],
            seerrServers: 0);

        Assert.Contains(problems, problem => problem.Contains("comes from Seerr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_a_whole_title_clears_it_from_Seerr_and_a_failure_does_not_undo_the_removal()
    {
        _seerr.Library = new SeerrLibrary([], []);
        _seerr.ClearProblem = "Deluno could not reach Lab Seerr";
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((MediaExclusionItem?)null);
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(15);

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        var removed = Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed);
        Assert.Contains("Seerr: Lab Seerr could not be cleared yet", removed.Message, StringComparison.Ordinal);
        Assert.Equal([("m1", "movies", (string?)"653")], _seerr.Cleared);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------- by hand (#1008)

    [Fact]
    public async Task Confirming_a_hand_added_title_in_ask_first_mode_removes_it_instead_of_discarding_its_mark()
    {
        // Nobody has watched the film, so the steps do not match it; a person added it by hand (#1069).
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653"));
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((MediaExclusionItem?)null);
        var rule = Rule(Keep("server.watchedBy:anyone:")) with { RemovalMode = RetentionRemovalModes.Ask, WaitDays = 1 };
        _store.Add(rule);
        await Service().AddManualAsync(rule, "m1", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal(RetentionMarkStates.Ready, Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).State);

        var confirmed = await Service().ConfirmAsync(rule, ["m1"], CancellationToken.None);

        Assert.Equal(1, confirmed.RemovedCount);
        Assert.DoesNotContain(confirmed.Items!, item => item.Outcome == RetentionItemOutcomes.Cleared);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_title_added_by_hand_is_marked_manual_and_the_rules_steps_no_longer_decide_it()
    {
        // Nobody has watched either film, so the rule's step matches neither.
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653"), Item("j2", Path("His Girl Friday", 1940), tmdb: "3085"));
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((MediaExclusionItem?)null);
        var rule = Rule(Keep("server.watchedBy:anyone:")) with { WaitDays = 3 };
        _store.Add(rule);
        var feed = new Mock<IActivityFeedRepository>();

        var added = await Service(feed).AddManualAsync(rule, "m1", CancellationToken.None);

        Assert.Null(added.Problem);
        Assert.True(added.Mark!.Manual);
        Assert.Equal((RetentionMarkStates.Leaving, Now.AddDays(3), new RetentionServerItem("server-1", "j1", MediaServerItemLevels.Movie, null)), (added.Mark.State, added.Mark.RemoveAfterUtc, added.Mark.ServerItems.Single()));
        Said(feed, "retention.added-by-hand", "Nosferatu was added by hand", Times.Once());

        // A check does not clear it for failing the steps, and does not mark the other film.
        var check = await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        var mark = Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
        Assert.Equal(("m1", true), (mark.EntityId, mark.Manual));
        Assert.DoesNotContain(check.Items!, item => item.Outcome == RetentionItemOutcomes.Cleared);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // It goes on its day, like anything else on the list.
        _clock.Now = Now.AddDays(4);
        var day = await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal("m1", Assert.Single(day.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
        _movies.Verify(repo => repo.DeleteAsync("m2", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_kept_title_cannot_be_added_and_taking_a_title_off_by_hand_keeps_the_rule_from_putting_it_back()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);
        var service = Service();
        await service.CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None));

        // Taking it off the list by hand is a keep from this rule.
        await service.KeepAsync(new SaveRetentionKeepRequest("m1", "movie", rule.Id, "Taken off this list by hand."), CancellationToken.None);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Empty(await _store.ListMarksAsync(rule.Id, CancellationToken.None));

        var again = await Service().AddManualAsync(rule, "m1", CancellationToken.None);
        Assert.Null(again.Mark);
        Assert.Contains("kept", again.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_by_hand_says_why_when_it_cannot()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653"));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);

        Assert.Equal("Turn the rule on first, so its list is checked.", (await Service().AddManualAsync(rule with { IsEnabled = false }, "m1", CancellationToken.None)).Problem);
        Assert.Equal("Choose a title to add.", (await Service().AddManualAsync(rule, " ", CancellationToken.None)).Problem);
        Assert.Contains("no title like that", (await Service().AddManualAsync(rule, "nope", CancellationToken.None)).Problem, StringComparison.Ordinal);

        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<MediaServerLibrary>.Unreachable("Deluno could not reach Rig lab Jellyfin."));
        Assert.Contains("could not reach", (await Service().AddManualAsync(rule, "m1", CancellationToken.None)).Problem, StringComparison.Ordinal);
        Assert.Empty(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Adding_a_title_the_rule_already_marked_makes_it_manual_without_moving_its_day()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        var rule = Rule(Keep("server.watchedBy:anyone:"));
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(5);

        var added = await Service().AddManualAsync(rule, "m1", CancellationToken.None);

        Assert.True(added.Mark!.Manual);
        Assert.Equal(Now.AddDays(14), Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).RemoveAfterUtc);
    }

    // ------------------------------------------------- postpone (#1010)

    [Fact]
    public async Task Postponing_pushes_the_day_back_without_keeping_the_title_and_nothing_goes_early()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { WaitDays = 14 });
        var feed = new Mock<IActivityFeedRepository>();
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        var moved = Assert.Single(await Service(feed).PostponeAsync(rule, ["m1"], 5, CancellationToken.None));

        Assert.Equal((Now.AddDays(19), RetentionMarkStates.Leaving), (moved.RemoveAfterUtc, moved.State));
        Said(feed, "retention.postponed", "Nosferatu was postponed by 5 days", Times.Once());
        Assert.Empty(await _store.ListKeepsAsync(CancellationToken.None));

        // The old day passes and it is still here; the new day comes and it goes.
        _clock.Now = Now.AddDays(15);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(Now.AddDays(19), Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).RemoveAfterUtc);

        _clock.Now = Now.AddDays(20);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_title_already_past_its_day_is_postponed_from_today_and_a_ready_one_goes_back_to_waiting()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { WaitDays = 1, RemovalMode = RetentionRemovalModes.Ask });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(10);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal(RetentionMarkStates.Ready, Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).State);

        var moved = Assert.Single(await Service().PostponeAsync(rule, ["m1"], 3, CancellationToken.None));

        Assert.Equal((Now.AddDays(13), RetentionMarkStates.Leaving), (moved.RemoveAfterUtc, moved.State));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(3650)]
    public async Task Postponing_only_ever_moves_a_day_later(int days)
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { WaitDays = 14 });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        var before = Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).RemoveAfterUtc;

        var after = Assert.Single(await Service().PostponeAsync(rule, ["m1"], days, CancellationToken.None)).RemoveAfterUtc;

        Assert.Equal(before.AddDays(days), after);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(3651)]
    public void Postponing_by_no_days_a_negative_number_or_too_many_is_refused(int? days)
    {
        Assert.Equal("Postpone by a whole number of days from 1 to 3650.", RetentionLimits.PostponeProblem(days));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3650)]
    public void Postponing_by_one_to_3650_days_is_accepted(int days)
    {
        Assert.Null(RetentionLimits.PostponeProblem(days));
    }

    [Fact]
    public async Task Postponing_something_not_on_the_list_or_already_done_changes_nothing()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { ExpiryAction = RetentionExpiryActions.Log });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal(RetentionMarkStates.Done, Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).State);

        Assert.Empty(await Service().PostponeAsync(rule, ["m1", "nope"], 5, CancellationToken.None));
    }

    // ------------------------------------------------- what happens on the day (#1011)

    [Fact]
    public async Task A_rule_that_says_nothing_about_its_action_removes_the_title_as_every_rule_did()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule);
        Assert.Equal(RetentionExpiryActions.Delete, rule.ExpiryAction);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(1, run.RemovedCount);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stop_monitoring_leaves_the_files_and_the_title_and_is_done_once()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { ExpiryAction = RetentionExpiryActions.Unmonitor });
        var feed = new Mock<IActivityFeedRepository>();
        var first = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal("Leaving soon: stops being monitored on 18 Sep 2026 (its files stay) unless it stops matching or is kept.", Assert.Single(first.Items!).Message);
        _clock.Now = Now.AddDays(2);

        var run = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);

        var done = Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Done);
        Assert.Equal("Stopped monitoring it; its files stay.", done.Message);
        Assert.Equal(0, run.RemovedCount);
        Assert.Equal("1 title stopped being monitored (files kept).", run.Summary);
        _movies.Verify(repo => repo.UpdateMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "m1" })), false, It.IsAny<CancellationToken>()), Times.Once);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _recycleBin.Verify(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        var mark = Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
        Assert.Equal(RetentionMarkStates.Done, mark.State);
        Said(feed, "retention.action", "Nosferatu: Stopped monitoring it; its files stay.", Times.Once());

        // Later checks find it still matching and leave the done title alone.
        _clock.Now = Now.AddDays(30);
        await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        _movies.Verify(repo => repo.UpdateMonitoredAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);

        // Changing the action starts afresh, so the new action is done after a new wait.
        await Service().ForgetDoneAsync(rule.Id, CancellationToken.None);
        Assert.Empty(await _store.ListMarksAsync(rule.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Stop_monitoring_a_show_or_a_season_uses_the_shows_or_the_episodes_and_never_the_files()
    {
        SeasonLibrary();
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1, ExpiryAction = RetentionExpiryActions.Unmonitor };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);

        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.Order().SequenceEqual(new[] { "e1", "e2" })), false, It.IsAny<CancellationToken>()), Times.Once);
        _recycleBin.Verify(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Just_log_changes_nothing_and_says_so_in_Activity_once()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { ExpiryAction = RetentionExpiryActions.Log });
        var feed = new Mock<IActivityFeedRepository>();
        await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);

        var run = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(9);
        await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("Its day came. The rule only records it, so nothing changed.", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Done).Message);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _movies.Verify(repo => repo.UpdateMonitoredAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Said(feed, "retention.action", "Its day came. The rule only records it", Times.Once());
    }

    [Fact]
    public async Task Tag_gives_the_title_a_Deluno_tag_and_nothing_else_changes()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { ExpiryAction = RetentionExpiryActions.Tag, ExpiryTag = "watched-and-old" });
        var tags = new Mock<ITitleTagService>();
        tags.Setup(service => service.GiveAsync(MediaKind.Movie, "m1", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(["watched-and-old"]);
        await Service(tags: tags.Object).CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);

        var run = await Service(tags: tags.Object).CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("Tagged “watched-and-old”.", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Done).Message);
        tags.Verify(service => service.GiveAsync(MediaKind.Movie, "m1", It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "watched-and-old" })), It.IsAny<CancellationToken>()), Times.Once);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Tagging_a_season_tags_its_show_and_a_failure_to_tag_is_reported_not_swallowed()
    {
        SeasonLibrary();
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1, ExpiryAction = RetentionExpiryActions.Tag, ExpiryTag = "seen" };
        _store.Add(rule);
        var tags = new Mock<ITitleTagService>();
        tags.Setup(service => service.GiveAsync(MediaKind.Series, "s1", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(["seen"]);
        await Service(tags: tags.Object).CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        await Service(tags: tags.Object).CheckAsync(rule, "schedule", CancellationToken.None);
        tags.Verify(service => service.GiveAsync(MediaKind.Series, "s1", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);

        // Without a tag service the title is not marked done: the failure is what the run says.
        var failing = Rule(Keep("server.watchedBy:anyone:")) with { Id = "rule-2", ExpiryAction = RetentionExpiryActions.Tag, ExpiryTag = "x", WaitDays = 1 };
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        _store.Add(failing);
        _clock.Now = Now;
        await Service().CheckAsync(failing, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        var run = await Service().CheckAsync(failing, "schedule", CancellationToken.None);
        Assert.Equal(RetentionItemOutcomes.Failed, Assert.Single(run.Items!).Outcome);
        Assert.Equal(RetentionMarkStates.Leaving, Assert.Single(await _store.ListMarksAsync(failing.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task A_rule_that_asks_first_does_the_chosen_action_when_a_person_confirms()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { ExpiryAction = RetentionExpiryActions.Unmonitor, RemovalMode = RetentionRemovalModes.Ask });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        var check = await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.StartsWith("Ready: waiting for you to confirm.", Assert.Single(check.Items!).Message, StringComparison.Ordinal);
        _movies.Verify(repo => repo.UpdateMonitoredAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        var confirmed = await Service().ConfirmAsync(rule, ["m1"], CancellationToken.None);

        Assert.Equal("1 title stopped being monitored (files kept).", confirmed.Summary);
        _movies.Verify(repo => repo.UpdateMonitoredAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_builders_preview_says_what_will_happen_on_the_day()
    {
        var rule = Rule(Keep("server.watchedBy:anyone:")) with { ExpiryAction = RetentionExpiryActions.Tag, ExpiryTag = "old" };

        Assert.Equal("It is given the tag “old”. Nothing else changes.", RetentionExpiryActions.Describe(rule.ExpiryAction, rule.ExpiryTag));
        Assert.Equal("Its files go to the recycle bin and it is removed.", RetentionExpiryActions.Describe(RetentionExpiryActions.Delete, null));
        // A value nobody knows is never a deletion.
        Assert.Equal(RetentionExpiryActions.Log, RetentionExpiryActions.Normalize("explode"));
        Assert.Equal(RetentionExpiryActions.Delete, RetentionExpiryActions.Normalize(null));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("explode", null, "Choose what happens on the day")]
    [InlineData("tag", null, "Name the tag")]
    [InlineData("tag", "a,b", "no commas")]
    public void The_action_and_its_tag_are_checked_when_a_rule_is_saved(string action, string? tag, string expected)
    {
        var problems = RetentionEvaluator.Validate(
            new SaveRetentionRuleRequest("Rule", RetentionLevels.Movie, true, [Keep("server.watchedBy:anyone:")], ["server-1"], false, ExpiryAction: action, ExpiryTag: tag),
            [_server]);

        Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    // Every connected server is asked, not only the ones the rule reads (#1012): the files are deleted for all of them.

    private static readonly string NosferatuPlayed = "c:/deluno/retentionlab/movies/NOSFERATU (1922)/nosferatu (1922).mkv";

    private async Task<(RetentionRule Rule, Mock<IActivityFeedRepository> Feed)> ReadyToRemoveWithAnotherServerAsync(MediaServerAnswer<IReadOnlyList<MediaServerPlay>> otherServerSays, bool otherEnabled = true)
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { DeferWhilePlaying = true });
        var other = _server with { Id = "server-2", Kind = "emby", Name = "Family Emby", IsEnabled = otherEnabled };
        _servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_server, other]);
        _otherPlayback.Setup(reader => reader.ListPlayingAsync(It.Is<MediaServerConnection>(server => server.Id == "server-2"), It.IsAny<CancellationToken>())).ReturnsAsync(otherServerSays);
        var feed = new Mock<IActivityFeedRepository>();
        await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        return (rule, feed);
    }

    [Fact]
    public async Task A_film_playing_on_a_server_the_rule_does_not_read_is_found_by_its_file_and_not_removed()
    {
        var (rule, feed) = await ReadyToRemoveWithAnotherServerAsync(new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["x1"], [NosferatuPlayed], true)]));

        var run = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(run.Items!).Outcome);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Said(feed, "retention.deferred", "it is being played right now", Times.Once());
    }

    [Fact]
    public async Task A_film_playing_under_another_path_is_found_by_its_provider_ids()
    {
        var (rule, _) = await ReadyToRemoveWithAnotherServerAsync(new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["x1"], ["/mnt/elsewhere/n.mkv"], true, TmdbId: "653")]));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(run.Items!).Outcome);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_session_that_matches_nothing_of_the_titles_does_not_hold_it_back()
    {
        var (rule, _) = await ReadyToRemoveWithAnotherServerAsync(new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported(
            [new MediaServerPlay(["x1"], ["/mnt/elsewhere/other.mkv"], true, TmdbId: "999", ImdbId: "tt0000001")]));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("m1", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_connected_server_that_cannot_be_asked_defers_the_removal_and_says_which()
    {
        var (rule, feed) = await ReadyToRemoveWithAnotherServerAsync(new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable("Deluno could not reach Family Emby."));

        var run = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);

        var held = Assert.Single(run.Items!);
        Assert.Equal(RetentionItemOutcomes.Held, held.Outcome);
        Assert.Contains("Family Emby", held.Message, StringComparison.Ordinal);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifications.Verify(service => service.DispatchAsync(RetentionService.WaitingUnreachableEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_disabled_server_is_not_asked_and_does_not_defer_anything()
    {
        var (rule, _) = await ReadyToRemoveWithAnotherServerAsync(new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable("off"), otherEnabled: false);

        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
        _otherPlayback.Verify(reader => reader.ListPlayingAsync(It.IsAny<MediaServerConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------- playing now (#1012)

    [Fact]
    public async Task A_title_being_played_is_not_removed_and_goes_at_the_next_check_once_it_stops()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { DeferWhilePlaying = true });
        var feed = new Mock<IActivityFeedRepository>();
        await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["j1"], [])]);

        var run = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);

        var held = Assert.Single(run.Items!);
        Assert.Equal((RetentionItemOutcomes.Held, "Not removed: it is being played right now. Deluno tries again at the next check."), (held.Outcome, held.Message));
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(RetentionMarkStates.Held, Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).State);
        Said(feed, "retention.deferred", "did not remove Nosferatu: it is being played right now", Times.Once());
        Assert.Contains("left alone while being played", run.Summary, StringComparison.Ordinal);

        // Still playing at the next check: still not removed.
        _clock.Now = Now.AddDays(2).AddHours(8);
        await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // It has stopped: the removal that was deferred goes ahead.
        _clock.Now = Now.AddDays(2).AddHours(16);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([]);
        var later = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal("m1", Assert.Single(later.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_what_is_playing_cannot_be_seen_nothing_is_removed()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { DeferWhilePlaying = true });
        var feed = new Mock<IActivityFeedRepository>();
        await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable("Deluno could not reach Rig lab Jellyfin.");

        var run = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);

        var held = Assert.Single(run.Items!);
        Assert.Equal(RetentionItemOutcomes.Held, held.Outcome);
        Assert.Contains("could not see what is being played", held.Message, StringComparison.Ordinal);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Said(feed, "retention.deferred", "could not see what is being played", Times.Once());
        _notifications.Verify(service => service.DispatchAsync(RetentionService.WaitingUnreachableEvent, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_server_that_cannot_say_what_is_playing_holds_the_removal()
    {
        // Emby-family and Plex answer; a server kind without the ability must not read as "nothing playing".
        var rule = await WatchedFilmRuleAsync(rule => rule with { DeferWhilePlaying = true });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.NotSupported("Rig lab Jellyfin cannot say what is being played.");

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(run.Items!).Outcome);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Something_else_playing_does_not_hold_a_title_back_and_a_rule_that_does_not_ask_never_asks()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { DeferWhilePlaying = true });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["j2", "somebody-elses-show"], [])]);

        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);

        // A rule from before the setting existed removes as it always did, without asking the server anything.
        _playback.Invocations.Clear();
        _clock.Now = Now;
        var old = Rule(Keep("server.watchedBy:anyone:")) with { Id = "rule-old", WaitDays = 1 };
        _store.Add(old);
        await Service().CheckAsync(old, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["j1"], [])]);
        await Service().CheckAsync(old, "schedule", CancellationToken.None);
        _playback.Verify(reader => reader.ListPlayingAsync(It.IsAny<MediaServerConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_season_is_held_while_an_episode_of_it_plays_but_not_while_another_season_does()
    {
        SeasonLibrary();
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((MediaExclusionItem?)null);
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1, DeferWhilePlaying = true };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);

        // Season 1 is what is matched; a play in season 2 (js2, its episode, the show) is not a play of season 1.
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["js2", "je3"], [])]);
        var other = await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal("s1:s1", Assert.Single(other.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
    }

    [Fact]
    public async Task A_season_is_held_while_one_of_its_episodes_plays()
    {
        SeasonLibrary();
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1, DeferWhilePlaying = true };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["je2", "js1", "js"], [])]);

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(run.Items!).Outcome);
        _recycleBin.Verify(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------- a Plex account is not everyone (#1074)

    private void OneAccountOnlyServer()
        => ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653") with { Watch = new MediaServerWatchState(false, [], null, PerUserKnown: false) },
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["james"], last: Now.AddDays(-3)) with { Watch = new MediaServerWatchState(true, ["james"], Now.AddDays(-3), PerUserKnown: false) });

    [Theory]
    [InlineData("server.watchedBy:nobody:")]
    [InlineData("server.watchedBy:notin:ben")]
    [InlineData("server.watchedBy:everyone:")]
    [InlineData("server.favouritedBy:nobody:")]
    [InlineData("server.timesViewed:max:0")]
    [InlineData("server.timesViewed:is:0")]
    [InlineData("server.lastViewed:unset:")]
    [InlineData("server.lastViewed:beyond:30")]
    public async Task On_a_server_that_reports_one_account_only_a_claim_about_nobody_else_decides_nothing(string condition)
    {
        OneAccountOnlyServer();

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep(condition)), CancellationToken.None);

        Assert.Empty(evaluation.Matched);
        Assert.Contains(evaluation.Candidates, item => item.EntityId == "m1" && item.Outcome == RetentionItemOutcomes.Undecided);
        Assert.Contains("only reports what the connected Plex account", evaluation.Candidates.First(item => item.EntityId == "m1").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("server.watchedBy:anyone:", "m2")]
    [InlineData("server.watchedBy:in:james", "m2")]
    [InlineData("server.timesViewed:min:1", "m2")]
    [InlineData("server.lastViewed:within:30", "m2")]
    public async Task On_a_server_that_reports_one_account_a_title_that_account_watched_is_still_found(string condition, string expected)
    {
        OneAccountOnlyServer();

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep(condition)), CancellationToken.None);

        Assert.Equal(expected, Assert.Single(evaluation.Matched).EntityId);
    }

    [Fact]
    public async Task A_server_that_reports_every_user_still_decides_watched_by_nobody()
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653"));

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.watchedBy:nobody:")), CancellationToken.None);

        Assert.Equal("m1", Assert.Single(evaluation.Matched).EntityId);
    }

    // ------------------------------------------------- lows from the integrity review (#1072)

    [Fact]
    public async Task A_mark_that_starts_being_held_is_said_once_in_Activity_and_not_again_while_it_stays_held()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", genres: ["Comedy"]),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", genres: ["Comedy"]));
        var rule = Rule(Keep("server.genres:notin:Horror")) with { WaitDays = 30 };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        // Nosferatu drops off the server: the rule can no longer tell, so its mark is held.
        ServerAnswers(Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", genres: ["Comedy"]));
        var feed = new Mock<IActivityFeedRepository>();
        var run = await Service(feed).CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(run.Items!, item => item.EntityId == "m1").Outcome);
        Said(feed, "retention.check", "held", Times.Once());

        var again = await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.DoesNotContain(again.Items!, item => item.Outcome == RetentionItemOutcomes.Held);
    }

    [Fact]
    public async Task Switching_a_rule_to_delete_starts_the_wait_of_marks_already_leaving_again_from_today()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { WaitDays = 3, ExpiryAction = RetentionExpiryActions.Tag, ExpiryTag = "old" });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        Assert.Equal(Now.AddDays(3), Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).RemoveAfterUtc);

        // Its promised day has come and gone before the rule is switched, so an unchanged mark would be removed at once.
        _clock.Now = Now.AddDays(4);
        var deleting = rule with { ExpiryAction = RetentionExpiryActions.Delete, ExpiryTag = null };
        var feed = new Mock<IActivityFeedRepository>();
        Assert.Equal(1, await Service(feed).RestartWaitForDeleteAsync(deleting, CancellationToken.None));

        Assert.Equal(Now.AddDays(7), Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).RemoveAfterUtc);
        Said(feed, "retention.restarted", "starts again from today", Times.Once());
        await Service().CheckAsync(deleting, "schedule", CancellationToken.None);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_check_cannot_run_between_saving_a_rule_as_delete_and_restarting_its_wait()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { WaitDays = 3, ExpiryAction = RetentionExpiryActions.Tag, ExpiryTag = "old" });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(4);

        // The save is slow; a scheduled check of the same rule arrives while it is still saving.
        var saving = new TaskCompletionSource();
        _store.BeforeSave = () => saving.Task;
        var save = Service().SaveEditedRuleAsync(rule.Id, new SaveRetentionRuleRequest(rule.Name, rule.Level, true, rule.Steps, rule.MediaServerIds, false, ExpiryAction: RetentionExpiryActions.Delete), CancellationToken.None);
        var check = Service().CheckAsync(rule with { ExpiryAction = RetentionExpiryActions.Delete, ExpiryTag = null }, "schedule", CancellationToken.None);
        await Task.Delay(100); // reason: shows the check is still waiting behind the slow save; absence needs real time
        Assert.False(check.IsCompleted);
        saving.SetResult();
        await save;
        await check;

        // The wait was restarted first, so the promised day (passed) did not remove anything.
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(Now.AddDays(7), Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).RemoveAfterUtc);
    }

    [Fact]
    public async Task A_queued_check_obeys_the_rule_as_edited_while_it_waited()
    {
        var stale = await WatchedFilmRuleAsync(rule => rule);
        // While the check sat queued the rule was edited to ask for films nobody has watched.
        _store.Add(stale with { Steps = [Keep("server.watchedBy:nobody:")] });

        var run = await Service().CheckAsync(stale, "schedule", CancellationToken.None);

        Assert.Empty(await _store.ListMarksAsync(stale.Id, CancellationToken.None));
        Assert.Equal(0, run.MatchedCount);
    }

    [Fact]
    public async Task A_queued_scheduled_check_does_nothing_when_the_rule_was_switched_off_or_deleted()
    {
        var stale = await WatchedFilmRuleAsync(rule => rule);
        _store.Add(stale with { IsEnabled = false });

        var off = await Service().CheckAsync(stale, "schedule", CancellationToken.None);
        Assert.Contains("switched off", off.Summary, StringComparison.Ordinal);
        Assert.Empty(await _store.ListMarksAsync(stale.Id, CancellationToken.None));
        Assert.Empty(_store.Runs);

        await _store.DeleteRuleAsync(stale.Id, CancellationToken.None);
        var gone = await Service().CheckAsync(stale, "schedule", CancellationToken.None);
        Assert.Contains("no longer exists", gone.Summary, StringComparison.Ordinal);
        Assert.Empty(await _store.ListMarksAsync(stale.Id, CancellationToken.None));
    }

    private static SeriesEpisodeInventoryItem AiredOrNot(string id, int season, int number, DateTimeOffset? aired)
        => new(id, season, number, null, null, aired, true, true, "", "", false, null, null, false, null, null, null, Now, null, null, null, null, null);

    private void SeasonOneEpisodes(params SeriesEpisodeInventoryItem[] episodes)
        => _series.Setup(repo => repo.GetInventoryDetailAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesInventoryDetail("s1", "Breaking Bad", 2008, 2, episodes.Length, episodes.Length, episodes));

    [Fact]
    public async Task A_season_and_an_episode_with_a_future_air_date_are_never_selected()
    {
        SeasonLibrary();
        // Season 1: every episode has a future air date. Upcoming, the movie word, so no rule touches it.
        SeasonOneEpisodes(AiredOrNot("e1", 1, 1, Now.AddDays(10)), AiredOrNot("e2", 1, 2, Now.AddDays(17)));

        var season = await Evaluator().EvaluateAsync(Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season }, CancellationToken.None);
        Assert.Empty(season.Matched);

        var episodes = await Evaluator().EvaluateAsync(Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Episode }, CancellationToken.None);
        Assert.DoesNotContain(episodes.Matched, match => match.EntityId is "e1" or "e2");
    }

    [Fact]
    public async Task A_season_with_something_aired_is_not_upcoming_and_an_aired_episode_can_still_be_selected()
    {
        SeasonLibrary();
        SeasonOneEpisodes(AiredOrNot("e1", 1, 1, Now.AddDays(-10)), AiredOrNot("e2", 1, 2, Now.AddDays(-9)));

        var season = await Evaluator().EvaluateAsync(Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season }, CancellationToken.None);
        Assert.Equal("s1:s1", Assert.Single(season.Matched).EntityId);

        // An episode with a future air date is Upcoming even inside a season that has aired; an undated one is not.
        SeasonOneEpisodes(AiredOrNot("e1", 1, 1, Now.AddDays(-10)), AiredOrNot("e2", 1, 2, null), AiredOrNot("e3", 1, 3, Now.AddDays(4)));
        var episodes = await Evaluator().EvaluateAsync(Rule(Keep("server.watchedBy:everyone:")) with { Level = RetentionLevels.Episode }, CancellationToken.None);
        Assert.Equal(["e1", "e2"], episodes.Matched.Select(match => match.EntityId));
    }

    [Fact]
    public async Task A_season_still_airing_loses_only_its_aired_episodes_and_the_rest_is_left_alone_and_said()
    {
        SeasonLibrary();
        // Season 1: e1 has aired and is watched; e2 has a file but its air date is still to come. Season 2: e3 aired.
        SeasonOneEpisodes(
            AiredOrNot("e1", 1, 1, Now.AddDays(-10)),
            AiredOrNot("e2", 1, 2, Now.AddDays(6)),
            AiredOrNot("e3", 2, 1, Now.AddDays(-5)));
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1 };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _clock.Now = Now.AddDays(2);
        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("s1:s1", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
        _recycleBin.Verify(bin => bin.MoveAsync(It.Is<IReadOnlyList<TrackedLibraryFile>>(files => files.Count == 1 && files[0].FilePath.Contains("S01E01")), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Once);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "e1" })), false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["e1"], (await _store.ListEpisodeRemovalsAsync("s1", CancellationToken.None)).Select(item => item.EpisodeId));
        Assert.Contains("not aired yet", run.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_show_still_airing_is_not_removed_as_a_whole_only_its_aired_episodes_go_one_by_one()
    {
        SeasonLibrary();
        SeasonOneEpisodes(
            AiredOrNot("e1", 1, 1, Now.AddDays(-10)),
            AiredOrNot("e2", 1, 2, Now.AddDays(-9)),
            AiredOrNot("e3", 2, 1, Now.AddDays(8)));
        _series.Setup(repo => repo.GetByIdAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync((SeriesListItem?)null);
        var rule = Rule(Keep("server.allEpisodesSeenBy:anyone:")) with { Level = RetentionLevels.Show, WaitDays = 1 };
        _store.Add(rule);
        Assert.Equal("s1", Assert.Single((await Evaluator().EvaluateAsync(rule, CancellationToken.None)).Matched).EntityId);

        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _series.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.Order().SequenceEqual(new[] { "e1", "e2" })), false, It.IsAny<CancellationToken>()), Times.Once);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.Contains("e3")), false, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_running_show_whose_next_season_has_no_dates_yet_is_not_removed_as_a_whole()
    {
        SeasonLibrary();
        // Season 3 is announced with dates to be announced: undated, no file. The show is still airing.
        SeasonOneEpisodes(
            AiredOrNot("e1", 1, 1, Now.AddDays(-10)),
            AiredOrNot("e2", 1, 2, Now.AddDays(-9)),
            AiredOrNot("e3", 2, 1, Now.AddDays(-5)),
            AiredOrNot("e4", 3, 1, null) with { HasFile = false });
        var rule = Rule(Keep("server.allEpisodesSeenBy:anyone:")) with { Level = RetentionLevels.Show, WaitDays = 1 };
        _store.Add(rule);

        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        _series.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.Order().SequenceEqual(new[] { "e1", "e2", "e3" })), false, It.IsAny<CancellationToken>()), Times.Once);
        _series.Verify(repo => repo.UpdateEpisodeMonitoredAsync(It.Is<IReadOnlyList<string>>(ids => ids.Contains("e4")), false, It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(run.Items!, item => (item.Message ?? "").Contains("Breaking Bad is gone", StringComparison.Ordinal));
        _jellyfin.Verify(client => client.RemoveAsync(_server, It.Is<MediaServerItem>(item => item.ServerItemId == "js"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Saving_a_rule_does_not_wait_for_a_check_that_is_still_reading_the_servers_and_that_check_then_changes_nothing()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { WaitDays = 1 });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);

        var reading = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>())).Returns(async () =>
        {
            reading.TrySetResult();
            await release.Task;
            return new MediaServerAnswer<MediaServerLibrary>.Reported(new MediaServerLibrary([Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40))], ["james", "ben"]));
        });

        var check = Service().CheckAsync(rule, "schedule", CancellationToken.None);
        await reading.Task;

        // The check is mid-read; switching the rule off must not queue behind it.
        var save = Service().SaveEditedRuleAsync(rule.Id, new SaveRetentionRuleRequest(rule.Name, rule.Level, false, rule.Steps, rule.MediaServerIds, false, ExpiryAction: RetentionExpiryActions.Delete), CancellationToken.None);
        Assert.Same(save, await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(5)))); // reason: the 5 s is the ceiling that proves the save did not queue behind the read, not a measured wait
        Assert.NotNull(await save);

        release.SetResult();
        var run = await check;

        Assert.Contains("edited or switched off", run.Summary, StringComparison.Ordinal);
        _recycleBin.Verify(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Checks_of_one_rule_run_one_at_a_time()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule);
        var active = 0;
        var most = 0;
        _jellyfin.Setup(client => client.ListItemsAsync(_server, "movies", It.IsAny<CancellationToken>())).Returns(async () =>
        {
            most = Math.Max(most, Interlocked.Increment(ref active));
            await Task.Delay(40); // reason: each read takes a moment so overlapping checks would show as more than one active at once
            Interlocked.Decrement(ref active);
            return new MediaServerAnswer<MediaServerLibrary>.Reported(new MediaServerLibrary([Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40))], ["james", "ben"]));
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Service().CheckAsync(rule, "manual", CancellationToken.None)));

        Assert.Equal(1, most);
    }

    // ------------------------------------------------- playing on another server, by show and numbers (#1071)

    private static MediaServerPlay EpisodePlay(string? showTmdb, int? season, int? episode, string? showName = "Breaking Bad", string? showTvdb = null)
        => new(["x1"], ["/mnt/elsewhere/ep.mkv"], IsEpisode: true, ShowName: showName, ShowTmdbId: showTmdb, ShowTvdbId: showTvdb, SeasonNumber: season, EpisodeNumber: episode);

    private async Task<RetentionRule> SeasonRuleWithAnotherServerPlayingAsync(MediaServerPlay play)
    {
        SeasonLibrary();
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((MediaExclusionItem?)null);
        var rule = Rule(Keep("server.allEpisodesSeenBy:everyone:")) with { Level = RetentionLevels.Season, WaitDays = 1, DeferWhilePlaying = true };
        _store.Add(rule);
        var other = _server with { Id = "server-2", Kind = "emby", Name = "Family Emby" };
        _servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_server, other]);
        _otherPlayback.Setup(reader => reader.ListPlayingAsync(It.Is<MediaServerConnection>(server => server.Id == "server-2"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([play]));
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        return rule;
    }

    [Fact]
    public async Task A_season_is_held_while_an_episode_of_its_show_plays_on_another_server_under_another_path()
    {
        var rule = await SeasonRuleWithAnotherServerPlayingAsync(EpisodePlay("1396", 1, 2));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(run.Items!).Outcome);
        _recycleBin.Verify(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_season_is_not_held_by_another_season_of_the_show_playing_elsewhere()
    {
        var rule = await SeasonRuleWithAnotherServerPlayingAsync(EpisodePlay("1396", 2, 1));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("s1:s1", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
    }

    [Fact]
    public async Task An_episode_playing_elsewhere_whose_show_cannot_be_told_defers_the_removal()
    {
        // No show ids from the server and a name that does not match: not knowing is not "nobody is watching".
        var rule = await SeasonRuleWithAnotherServerPlayingAsync(EpisodePlay(null, 1, 2, showName: "Something Else"));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(run.Items!).Outcome);
    }

    [Fact]
    public async Task A_different_show_playing_elsewhere_by_its_own_ids_does_not_hold_the_season_back()
    {
        var rule = await SeasonRuleWithAnotherServerPlayingAsync(EpisodePlay("9999", 1, 1, showName: "Other Show"));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal("s1:s1", Assert.Single(run.Items!, item => item.Outcome == RetentionItemOutcomes.Removed).EntityId);
    }

    [Theory]
    [InlineData(RetentionLevels.Show, 1, null, true)]
    [InlineData(RetentionLevels.Season, 1, null, true)]
    [InlineData(RetentionLevels.Season, 2, null, false)]
    [InlineData(RetentionLevels.Episode, 1, 2, true)]
    [InlineData(RetentionLevels.Episode, 1, 3, false)]
    public void Episode_plays_are_matched_by_the_shows_ids_and_the_season_and_episode_numbers(string level, int season, int? episode, bool held)
    {
        var unit = new RetentionUnit(level, "x", "s1", season, episode, "t", "Breaking Bad", 2008, null, "1396", null, null, [], []);

        Assert.Equal(held, RetentionService.EpisodePlayTouches(unit, EpisodePlay("1396", 1, 2)));
    }

    [Fact]
    public void An_unnumbered_episode_play_of_the_same_show_holds_a_season_or_episode()
    {
        var unit = new RetentionUnit(RetentionLevels.Episode, "x", "s1", 1, 1, "t", "Breaking Bad", 2008, null, "1396", null, null, [], []);

        Assert.True(RetentionService.EpisodePlayTouches(unit, EpisodePlay("1396", null, null)));
        Assert.True(RetentionService.EpisodePlayTouches(unit, EpisodePlay(null, 1, 1, showTvdb: null)));
    }

    [Theory]
    [InlineData("653", null, null, true)]      // the same film by TMDb
    [InlineData("9999", null, null, false)]    // a different film by the same kind of id
    [InlineData("9999", "tt0013442", null, true)] // TMDb differs but IMDb agrees: shares an id
    [InlineData(null, null, null, true)]       // no ids at all and a different path: cannot tell, so wait
    public void A_film_playing_on_another_server_is_held_unless_its_ids_say_it_is_a_different_film(string? tmdb, string? imdb, string? tvdb, bool held)
    {
        var unit = new RetentionUnit(RetentionLevels.Movie, "x", "m1", null, null, "t", "Nosferatu", 1922, "tt0013442", "653", null, null, [], []);
        var play = new MediaServerPlay(["other-1"], ["/elsewhere/Nosferatu.mkv"], IsMovie: true, TmdbId: tmdb, ImdbId: imdb, TvdbId: tvdb);

        Assert.Equal(held, RetentionService.MoviePlayTouches(unit, play));
    }

    [Fact]
    public async Task Playback_is_read_again_before_every_removal_so_a_play_that_starts_mid_run_is_seen()
    {
        ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)),
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085", playedBy: ["james"], last: Now.AddDays(-40)));
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((MediaExclusionItem?)null);
        var rule = Rule(Keep("server.watchedBy:anyone:")) with { WaitDays = 1, DeferWhilePlaying = true };
        _store.Add(rule);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);

        // First reading: nothing plays. Every later reading: His Girl Friday has just started.
        var readings = 0;
        _playback.Setup(reader => reader.ListPlayingAsync(_server, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported(++readings == 1 ? [] : [new MediaServerPlay(["j2"], [])]));

        var run = await Service().CheckAsync(rule, "schedule", CancellationToken.None);

        Assert.Equal(2, readings);
        Assert.Equal(1, run.Items!.Count(item => item.Outcome == RetentionItemOutcomes.Removed));
        Assert.Equal(1, run.Items!.Count(item => item.Outcome == RetentionItemOutcomes.Held));
        _movies.Verify(repo => repo.DeleteAsync("m2", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_person_confirming_a_removal_does_not_remove_what_is_playing_either()
    {
        var rule = await WatchedFilmRuleAsync(rule => rule with { DeferWhilePlaying = true, RemovalMode = RetentionRemovalModes.Ask });
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _clock.Now = Now.AddDays(2);
        await Service().CheckAsync(rule, "schedule", CancellationToken.None);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([new MediaServerPlay(["j1"], [])]);

        var confirmed = await Service().ConfirmAsync(rule, ["m1"], CancellationToken.None);

        Assert.Equal(RetentionItemOutcomes.Held, Assert.Single(confirmed.Items!).Outcome);
        Assert.Equal(0, confirmed.RemovedCount);
        _movies.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // It is still Ready to remove; confirming again after it has stopped removes it.
        Assert.Equal(RetentionMarkStates.Ready, Assert.Single(await _store.ListMarksAsync(rule.Id, CancellationToken.None)).State);
        _playing = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported([]);
        await Service().ConfirmAsync(rule, ["m1"], CancellationToken.None);
        _movies.Verify(repo => repo.DeleteAsync("m1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // #1068: a value nobody reported is undecided, and undecided never deletes.
    private void CodecLibrary()
        => ServerAnswers(
            Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)) with { Video = new MediaServerVideo("1080p", 8000, "hevc") },
            Item("j2", Path("His Girl Friday", 1940), tmdb: "3085") with { Video = new MediaServerVideo("1080p", 8000, "h264") },
            // Detour: the server never said what the video is.
            Item("j3", Path("Detour", 1945), tmdb: "20367", playedBy: ["ben"], last: Now.AddDays(-40)));

    [Fact]
    public async Task A_negative_rule_never_selects_a_film_whose_codec_the_server_did_not_report()
    {
        CodecLibrary();

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.codec:isnot:hevc")), CancellationToken.None);

        Assert.Equal("m2", Assert.Single(evaluation.Matched).EntityId);
        var undecided = Assert.Single(evaluation.Candidates, item => item.Outcome == RetentionItemOutcomes.Undecided);
        Assert.Equal("m3", undecided.EntityId);
    }

    [Fact]
    public async Task An_unknown_codec_stays_undecided_when_another_keep_step_holds()
    {
        CodecLibrary();

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.watchedBy:anyone:"), Keep("server.codec:nothas:hevc")), CancellationToken.None);

        Assert.Empty(evaluation.Matched);
        Assert.Equal("m3", Assert.Single(evaluation.Candidates, item => item.Outcome == RetentionItemOutcomes.Undecided).EntityId);
    }

    [Fact]
    public async Task An_unknown_step_does_not_stop_a_later_also_include_from_deciding_the_title()
    {
        CodecLibrary();
        var include = new RetentionStep(RetentionStep.ConditionType, RetentionStep.Include, "server.watchedBy:anyone:");

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("server.codec:is:hevc"), include), CancellationToken.None);

        // Nosferatu is hevc; Detour is watched, so the second step decides it; His Girl Friday is h264 and unwatched.
        Assert.Equal(["m1", "m3"], evaluation.Matched.Select(item => item.EntityId).Order());
        Assert.DoesNotContain(evaluation.Candidates, item => item.Outcome == RetentionItemOutcomes.Undecided);
    }

    [Fact]
    public async Task A_title_whose_library_value_is_blank_is_undecided_for_a_negative_library_test()
    {
        // The catalogue says every film is "not Horror", but only Nosferatu has a genre filled in.
        _movies.Setup(repo => repo.ListPageAsync(It.IsAny<CatalogueQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CatalogueQuery query, CancellationToken _) =>
            {
                var isSet = query.Filters?.Conditions is [{ Operator: CatalogueFilterOperator.IsSet }];
                var items = isSet ? _catalogue.Where(movie => movie.Id == "m1").ToArray() : _catalogue.ToArray();
                return new CataloguePage<MovieListItem>(items, null, false, items.Length, null);
            });

        var evaluation = await Evaluator().EvaluateAsync(Rule(Keep("genre:notin:Horror")), CancellationToken.None);

        Assert.Equal("m1", Assert.Single(evaluation.Matched).EntityId);
        Assert.Equal(["m2", "m3"], evaluation.Candidates.Where(item => item.Outcome == RetentionItemOutcomes.Undecided).Select(item => item.EntityId).Order());
    }

    private RetentionEvaluator Evaluator()
        => new(
            new RetentionUnitReader(_movies.Object, _series.Object, _libraries.Object, _clock),
            _servers.Object,
            new MediaServerClientRegistry([_jellyfin.Object]),
            _disk,
            _seerr,
            _clock,
            _qualityModel.Object);

    private RetentionService Service(Mock<IActivityFeedRepository>? feed = null, ITitleTagService? tags = null)
    {
        var intake = new Mock<IIntakeRepository>();
        var removal = new TitleRemovalService(_libraries.Object, intake.Object, Mock.Of<IJobQueueRepository>(), Mock.Of<IActivityFeedRepository>(), _recycleBin.Object);
        return new RetentionService(_store, Evaluator(), removal, _movies.Object, _series.Object, _libraries.Object, _recycleBin.Object, _exclusions.Object,
            new MediaServerClientRegistry([_jellyfin.Object, _other.Object]), _seerr, (feed ?? new Mock<IActivityFeedRepository>()).Object, _notifications.Object, _clock, NullLogger<RetentionService>.Instance, _notificationPreferences.Object,
            null, tags, _servers.Object);
    }

    private static void Said(Mock<IActivityFeedRepository> feed, string category, string containing, Times times)
        => feed.Verify(
            repo => repo.RecordActivityAsync(category, It.Is<string>(message => message.Contains(containing, StringComparison.Ordinal)), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            times);

    /// <summary>A watched film that has waited out its days, ready for a rule to act on.</summary>
    private async Task<RetentionRule> WatchedFilmRuleAsync(Func<RetentionRule, RetentionRule> shape)
    {
        ServerAnswers(Item("j1", Path("Nosferatu", 1922), tmdb: "653", playedBy: ["james"], last: Now.AddDays(-40)));
        _exclusions.Setup(repo => repo.UpsertAsync(It.IsAny<UpsertMediaExclusionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((MediaExclusionItem?)null);
        var rule = shape(Rule(Keep("server.watchedBy:anyone:")) with { WaitDays = 1 });
        _store.Add(rule);
        return rule;
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
