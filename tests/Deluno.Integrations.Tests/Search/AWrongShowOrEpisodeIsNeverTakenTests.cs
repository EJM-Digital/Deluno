using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;
using Moq;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #1159, #1166, #1173 and #1147 from the rc.30 live test, with the real release names from those issues and the
/// real decision engine: a same-name remake, another episode's title, a refused release named as the best, and a
/// download client chosen by something other than its priority.
/// </summary>
public sealed class AWrongShowOrEpisodeIsNeverTakenTests
{
    private static ReleaseTitleExpectation TwilightZone1959(int season = 1, int episode = 9)
        => new("The Twilight Zone", 1959, IsTv: true, season, episode);

    // ── #1166: a year that is not the series' year ──

    [Fact]
    public void The_1959_Twilight_Zone_refuses_the_2019_reboot_and_takes_its_own_episode()
    {
        var refused = ReleaseTitleMatch.Mismatch("The.Twilight.Zone.2019.S01E09.1080p.WEB.x264-worldmkv", TwilightZone1959());
        Assert.NotNull(refused);
        Assert.Contains("(2019), not (1959)", refused, StringComparison.Ordinal);

        Assert.Null(ReleaseTitleMatch.Mismatch("The Twilight Zone S01E09 Perchance to Dream 1080p WEB-DL x264-GRP", TwilightZone1959()));
        Assert.Null(ReleaseTitleMatch.Mismatch("The.Twilight.Zone.1959.S01E09.1080p.BluRay.x264-GRP", TwilightZone1959()));
    }

    [Theory]
    [InlineData("The.Twilight.Zone.2019.S01E10.Blurryman.REPACK.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb", 10)]
    [InlineData("The Twilight Zone 2019 S01E01 1080p WEB-DL x264 [562MB] [MP4]", 1)]
    [InlineData("The.Twilight.Zone.2019.S01E01.The.Comedian.1080p.WEB-DL.h264-P2P[TGx]", 1)]
    public void Every_reboot_release_from_the_overnight_loop_is_refused(string release, int episode)
        => Assert.NotNull(ReleaseTitleMatch.Mismatch(release, TwilightZone1959(1, episode)));

    [Fact]
    public void Battlestar_Galactica_1978_refuses_the_2004_series_and_the_reverse()
    {
        var original = new ReleaseTitleExpectation("Battlestar Galactica", 1978, IsTv: true, 1, 1);
        var reimagined = new ReleaseTitleExpectation("Battlestar Galactica", 2004, IsTv: true, 1, 1);

        Assert.NotNull(ReleaseTitleMatch.Mismatch("Battlestar.Galactica.2004.S01E01.1080p.BluRay.x264-GRP", original));
        Assert.Null(ReleaseTitleMatch.Mismatch("Battlestar.Galactica.2004.S01E01.1080p.BluRay.x264-GRP", reimagined));
        Assert.NotNull(ReleaseTitleMatch.Mismatch("Battlestar.Galactica.1978.S01E01.720p.HDTV.x264-GRP", reimagined));
        Assert.Null(ReleaseTitleMatch.Mismatch("Battlestar.Galactica.1978.S01E01.720p.HDTV.x264-GRP", original));
    }

    [Fact]
    public void A_daily_show_named_by_its_air_date_is_not_read_as_a_wrong_year()
    {
        var expected = new ReleaseTitleExpectation("The Daily Show", 1996, IsTv: true, AirDate: new DateOnly(2024, 5, 1));
        Assert.Null(ReleaseTitleMatch.Mismatch("The.Daily.Show.2024.05.01.Guest.1080p.WEB.h264-GRP", expected));
    }

    [Fact]
    public void A_release_with_the_right_year_is_preferred_to_one_with_none()
    {
        var expected = TwilightZone1959();
        Assert.True(ReleaseTitleMatch.CarriesSeriesYear("The.Twilight.Zone.1959.S01E09.1080p.BluRay.x264-GRP", expected));
        Assert.False(ReleaseTitleMatch.CarriesSeriesYear("The Twilight Zone S01E09 1080p WEB-DL x264-GRP", expected));
    }

    // ── #1173: another episode's title ──

    private static readonly IReadOnlyDictionary<int, string> Simpsons38 = new Dictionary<int, string>
    {
        [1] = "The Children's Book Job",
        [2] = "Diary of a Chimpy Kid",
        [3] = "Something Else Entirely"
    };

    private static ReleaseTitleExpectation Simpsons(int episode)
        => new("The Simpsons", 1989, IsTv: true, 38, episode, EpisodeTitles: Simpsons38);

    [Fact]
    public void An_episode_number_with_another_episodes_title_is_refused_for_that_reason()
    {
        var refused = ReleaseTitleMatch.Mismatch(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX", Simpsons(2));

        Assert.NotNull(refused);
        Assert.Contains("The name says S38E02, but its title is S38E01's \"The Children's Book Job\"", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void The_right_title_and_a_name_without_a_title_are_both_accepted()
    {
        Assert.Null(ReleaseTitleMatch.Mismatch("The Simpsons S38E02 Diary of a Chimpy Kid 1080p FOX WEB H264-ETHEL", Simpsons(2)));
        Assert.Null(ReleaseTitleMatch.Mismatch("The Simpsons S38E02 1080p WEB H264-GRP", Simpsons(2)));
        Assert.Null(ReleaseTitleMatch.Mismatch("The.Simpsons.S38E01.The.Childrens.Book.Job.1080p.DSNP.WEB-DL.DDP5.1.H.264-STRiKES", Simpsons(1)));
    }

    [Theory]
    [InlineData("The.Simpsons.S38E02.The.Children's.Book.Job.1080p.WEB.h264-GRP")]
    [InlineData("The.Simpsons.S38E02.Childrens.Book.Job.1080p.WEB.h264-GRP")]
    [InlineData("The.Simpsons.S38E02.THE.CHILDRENS.BOOK.JOB.REPACK.1080p.WEB.h264-GRP")]
    public void Punctuation_apostrophes_and_articles_do_not_hide_another_episodes_title(string release)
        => Assert.NotNull(ReleaseTitleMatch.Mismatch(release, Simpsons(2)));

    [Fact]
    public void A_search_of_the_whole_show_checks_a_release_against_its_own_episode_number_and_title()
    {
        var wholeShow = new ReleaseTitleExpectation(
            "The Simpsons", 1989, IsTv: true,
            SeasonEpisodeTitles: new Dictionary<int, IReadOnlyDictionary<int, string>> { [38] = Simpsons38 });

        var refused = ReleaseTitleMatch.Mismatch(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX", wholeShow);
        Assert.NotNull(refused);
        Assert.Contains("The name says S38E02, but its title is S38E01's \"The Children's Book Job\"", refused, StringComparison.Ordinal);

        Assert.Null(ReleaseTitleMatch.Mismatch("The Simpsons S38E02 Diary of a Chimpy Kid 1080p FOX WEB H264-ETHEL", wholeShow));
        Assert.Null(ReleaseTitleMatch.Mismatch("The Simpsons S38E02 1080p WEB H264-GRP", wholeShow));
        // A season pack or a season the provider has no titles for is not refused here.
        Assert.Null(ReleaseTitleMatch.Mismatch("The Simpsons S38 1080p WEB H264-GRP", wholeShow));
        Assert.Null(ReleaseTitleMatch.Mismatch("The Simpsons S12E02 The Childrens Book Job 1080p WEB H264-GRP", wholeShow));
    }

    [Fact]
    public void And_and_an_ampersand_are_the_same_word()
    {
        var titles = new Dictionary<int, string> { [1] = "Salt & Pepper", [2] = "Another One" };
        var expected = new ReleaseTitleExpectation("Some Show", 2020, IsTv: true, 1, 2, EpisodeTitles: titles);

        Assert.NotNull(ReleaseTitleMatch.Mismatch("Some.Show.S01E02.Salt.and.Pepper.1080p.WEB.h264-GRP", expected));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E02.Another.One.1080p.WEB.h264-GRP", expected));
    }

    [Fact]
    public void A_release_naming_the_episodes_own_title_is_preferred_to_one_with_none()
    {
        Assert.True(ReleaseTitleMatch.CarriesWantedEpisodeTitle("The Simpsons S38E02 Diary of a Chimpy Kid 1080p FOX WEB H264-ETHEL", Simpsons(2)));
        Assert.False(ReleaseTitleMatch.CarriesWantedEpisodeTitle("The Simpsons S38E02 1080p WEB H264-GRP", Simpsons(2)));
    }

    [Fact]
    public void The_real_engine_rejects_the_mislabelled_release_and_leads_with_that_reason()
    {
        var mismatch = ReleaseTitleMatch.Mismatch("The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX", Simpsons(2));

        var decision = Decide("The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX", "Bluray 1080p", mismatch);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains("but its title is", decision.Reasons[0], StringComparison.Ordinal);
        // The tier line that read as the rejection reason comes after the real one (#1159).
        Assert.StartsWith("Quality", decision.Reasons.First(reason => reason.StartsWith("Quality", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain(decision.Reasons, reason => reason.Contains("Typed preference evaluation", StringComparison.Ordinal));
    }

    // ── #1159: a refused release is never the best candidate ──

    private static MediaSearchCandidate Candidate(string name, string tier, string? mismatch = null, string? token = null, int? seeders = 20)
    {
        var decision = Decide(token ?? name, tier, mismatch, seeders);
        return new MediaSearchCandidate(
            ReleaseName: name,
            IndexerId: "idx",
            IndexerName: "Indexer",
            Quality: tier,
            Score: 0,
            MeetsCutoff: decision.MeetsCutoff,
            Summary: decision.Summary,
            DownloadUrl: "magnet:?xt=urn:btih:1",
            SizeBytes: 2_000_000_000,
            Seeders: seeders,
            DecisionStatus: decision.Status,
            DecisionReasons: decision.Reasons,
            RiskFlags: decision.RiskFlags,
            QualityDelta: decision.QualityDelta,
            RejectionGate: decision.RejectionGate);
    }

    private static ReleaseDecision Decide(string release, string target, string? mismatch, int? seeders = 20)
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: release,
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: target,
            SizeBytes: 2_000_000_000,
            Seeders: seeders,
            DownloadUrl: "magnet:?xt=urn:btih:1",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            IndexerProtocol: "tracker",
            PreferencePlan: ReleasePreferencePlanFactory.CreateQualityPlan("tv", target, null, upgradeUntilCutoff: true),
            MediaType: "tv",
            TitleMismatch: mismatch));

    [Fact]
    public void A_rejected_candidate_ranked_first_never_blocks_the_valid_one_behind_it()
    {
        // The Wire: a Chinese series from Nyaa sorted ahead of the real TARS episode.
        var other = Candidate(
            "[Shridhuu][4K] GuAn / One Slash to Cleave the Heavens - S01E11", "WEB 1080p",
            ReleaseTitleMatch.Mismatch("[Shridhuu][4K] GuAn / One Slash to Cleave the Heavens - S01E11", new ReleaseTitleExpectation("The Wire", 2002, IsTv: true, 1, 11)));
        var wire = Candidate("The.Wire.S01E11.1080p.WEBRip.H264-TARS", "WEB 1080p");
        var plan = new MediaSearchPlan(other, [other, wire], "Best feed candidate is the other show.");

        var fixedPlan = AcquisitionDecisionPipeline.NeverNameARejectedCandidateBest(plan);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, other.DecisionStatus);
        Assert.Equal(wire.ReleaseName, fixedPlan.BestCandidate?.ReleaseName);
        Assert.True(AcquisitionDecisionPipeline.IsSafeForAutomaticDispatch(fixedPlan.BestCandidate!) || fixedPlan.BestCandidate!.DecisionStatus != ReleaseDecisionStatuses.Rejected);
        Assert.Equal(2, fixedPlan.Candidates.Count);
    }

    [Fact]
    public async Task When_every_candidate_is_refused_the_plan_says_so_plainly_with_the_leading_reasons()
    {
        var anime = Candidate(
            "Resident Evil Infinite Darkness S01 MULTi 1080p NF WEB-DL", "Bluray 1080p",
            ReleaseTitleMatch.Mismatch("Resident Evil Infinite Darkness S01 MULTi 1080p NF WEB-DL", new ReleaseTitleExpectation("Resident Evil", 2026, IsTv: false)));
        var cam1 = Candidate("Resident.Evil.2026.CAM.x264-GRP", "Bluray 1080p");
        var cam2 = Candidate("Resident.Evil.2026.HDTS.x264-GRP", "Bluray 1080p");
        var unseeded = Candidate("Resident.Evil.2026.1080p.WEB.x264-GRP", "Bluray 1080p", seeders: 0);
        MediaSearchCandidate[] all = [anime, cam1, cam2, unseeded];
        Assert.All(all, candidate => Assert.Equal(ReleaseDecisionStatuses.Rejected, candidate.DecisionStatus));
        var plan = new MediaSearchPlan(anime, all, "Best feed candidate is Resident Evil Infinite Darkness.");

        var pipeline = new AcquisitionDecisionPipeline(new FixedPlanner(plan));
        var decision = await pipeline.PlanAsync(Request());

        Assert.Null(decision.SearchPlan.BestCandidate);
        Assert.Equal("checked", decision.Outcome);
        Assert.False(decision.ShouldDispatch);
        Assert.StartsWith("No usable release yet:", decision.SearchResult, StringComparison.Ordinal);
        Assert.Contains("2 were cams, telesyncs or samples", decision.SearchResult, StringComparison.Ordinal);
        Assert.DoesNotContain("manual review", decision.SearchResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Resident Evil Infinite Darkness", decision.SearchResult, StringComparison.Ordinal);
        Assert.Equal(4, decision.SearchPlan.Candidates.Count);
    }

    // ── #1147: priority wins, equals take turns ──

    [Fact]
    public async Task Three_clients_of_equal_priority_take_three_grabs_in_turn()
    {
        var clients = Clients(("a-" + Guid.NewGuid().ToString("n"), 10), ("b-" + Guid.NewGuid().ToString("n"), 10), ("c-" + Guid.NewGuid().ToString("n"), 10));
        var pipeline = new AcquisitionDecisionPipeline(new FixedPlanner(OnePreferred()));

        var chosen = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var plan = await pipeline.PlanAsync(Request(clients));
            Assert.True(plan.ShouldDispatch);
            Assert.Contains("its turn", plan.ClientChoiceReason, StringComparison.Ordinal);
            chosen.Add(plan.SelectedDownloadClient!.DownloadClientId);
        }

        Assert.Equal(3, chosen.Distinct().Count());
        // And the cycle goes round again.
        var fourth = await pipeline.PlanAsync(Request(clients));
        Assert.Equal(chosen[0], fourth.SelectedDownloadClient!.DownloadClientId);
    }

    [Fact]
    public async Task A_preview_does_not_use_up_a_turn()
    {
        var clients = Clients(("a-" + Guid.NewGuid().ToString("n"), 10), ("b-" + Guid.NewGuid().ToString("n"), 10));
        var pipeline = new AcquisitionDecisionPipeline(new FixedPlanner(OnePreferred()));

        var first = await pipeline.PlanAsync(Request(clients));
        await pipeline.PlanAsync(Request(clients) with { PreviewOnly = true });
        await pipeline.PlanAsync(Request(clients) with { PreviewOnly = true });
        var second = await pipeline.PlanAsync(Request(clients));

        Assert.NotEqual(first.SelectedDownloadClient!.DownloadClientId, second.SelectedDownloadClient!.DownloadClientId);
    }

    [Fact]
    public async Task A_lower_priority_number_always_wins_whatever_the_order_or_history()
    {
        var qbit = "qbit-" + Guid.NewGuid().ToString("n");
        var deluge = "deluge-" + Guid.NewGuid().ToString("n");
        // Deluge listed first, with a perfect record; qBittorrent has the better priority and no record at all.
        var routing = new Mock<IIntelligentRoutingService>();
        routing.Setup(service => service.GetDownloadClientSuccessRateAsync(deluge, It.IsAny<CancellationToken>())).ReturnsAsync(1.0);
        routing.Setup(service => service.GetDownloadClientSuccessRateAsync(qbit, It.IsAny<CancellationToken>())).ReturnsAsync((double?)null);
        var pipeline = new AcquisitionDecisionPipeline(new FixedPlanner(OnePreferred()), intelligentRoutingService: routing.Object);

        for (var i = 0; i < 3; i++)
        {
            var plan = await pipeline.PlanAsync(Request(Clients((deluge, 50), (qbit, 10))));
            Assert.Equal(qbit, plan.SelectedDownloadClient!.DownloadClientId);
            Assert.Contains("highest priority (10)", plan.ClientChoiceReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_failing_client_is_passed_over_but_one_with_no_record_is_not_worse()
    {
        var failing = "failing-" + Guid.NewGuid().ToString("n");
        var fresh = "fresh-" + Guid.NewGuid().ToString("n");
        var routing = new Mock<IIntelligentRoutingService>();
        routing.Setup(service => service.GetDownloadClientSuccessRateAsync(failing, It.IsAny<CancellationToken>())).ReturnsAsync(0.1);
        routing.Setup(service => service.GetDownloadClientSuccessRateAsync(fresh, It.IsAny<CancellationToken>())).ReturnsAsync((double?)null);
        var pipeline = new AcquisitionDecisionPipeline(new FixedPlanner(OnePreferred()), intelligentRoutingService: routing.Object);

        // The failing one has the better priority and is still skipped.
        var plan = await pipeline.PlanAsync(Request(Clients((failing, 10), (fresh, 30))));

        Assert.Equal(fresh, plan.SelectedDownloadClient!.DownloadClientId);
        Assert.Contains("passed over", plan.ClientChoiceReason, StringComparison.Ordinal);

        // With every client failing, priority decides again rather than refusing to send.
        routing.Setup(service => service.GetDownloadClientSuccessRateAsync(fresh, It.IsAny<CancellationToken>())).ReturnsAsync(0.2);
        var all = await pipeline.PlanAsync(Request(Clients((failing, 10), (fresh, 30))));
        Assert.Equal(failing, all.SelectedDownloadClient!.DownloadClientId);
    }

    // ── #1222: the decision text names the rate and its window; a passed-over client gets a trial send after the cool-off ──

    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-10-10T12:00:00Z");

    private sealed class ClockAt(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Mock<IIntelligentRoutingService> Routing(string id, DownloadClientRecord? record)
    {
        var routing = new Mock<IIntelligentRoutingService>();
        routing.Setup(service => service.GetDownloadClientRecordAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        routing.Setup(service => service.GetDownloadClientSuccessRateAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(record?.Rate);
        return routing;
    }

    [Fact]
    public async Task A_passed_over_client_is_named_with_its_rate_and_the_window_it_was_worked_out_over()
    {
        var failing = "failing-" + Guid.NewGuid().ToString("n");
        var fresh = "fresh-" + Guid.NewGuid().ToString("n");
        var routing = Routing(failing, new DownloadClientRecord(10, 2, 0.2, 30, Noon.AddHours(-1)));
        routing.Setup(service => service.GetDownloadClientRecordAsync(fresh, It.IsAny<CancellationToken>())).ReturnsAsync((DownloadClientRecord?)null);
        routing.Setup(service => service.GetDownloadClientSuccessRateAsync(fresh, It.IsAny<CancellationToken>())).ReturnsAsync((double?)null);
        var pipeline = new AcquisitionDecisionPipeline(new FixedPlanner(OnePreferred()), intelligentRoutingService: routing.Object, timeProvider: new ClockAt(Noon));

        var plan = await pipeline.PlanAsync(Request(Clients((failing, 10), (fresh, 30))));

        Assert.Equal(fresh, plan.SelectedDownloadClient!.DownloadClientId);
        Assert.Contains($"{failing} was passed over: 2 of its last 10 downloads imported (counting the last 30 days)", plan.ClientChoiceReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_passed_over_client_gets_a_trial_send_once_the_cool_off_has_passed_and_not_before()
    {
        var failing = "failing-" + Guid.NewGuid().ToString("n");
        var fresh = "fresh-" + Guid.NewGuid().ToString("n");

        async Task<AcquisitionDecisionPlan> PlanWithLastSend(TimeSpan sinceLastSend)
        {
            var routing = Routing(failing, new DownloadClientRecord(10, 2, 0.2, 30, Noon - sinceLastSend));
            routing.Setup(service => service.GetDownloadClientRecordAsync(fresh, It.IsAny<CancellationToken>())).ReturnsAsync((DownloadClientRecord?)null);
            routing.Setup(service => service.GetDownloadClientSuccessRateAsync(fresh, It.IsAny<CancellationToken>())).ReturnsAsync((double?)null);
            var pipeline = new AcquisitionDecisionPipeline(new FixedPlanner(OnePreferred()), intelligentRoutingService: routing.Object, timeProvider: new ClockAt(Noon));
            return await pipeline.PlanAsync(Request(Clients((failing, 10), (fresh, 30))));
        }

        var tooSoon = await PlanWithLastSend(DispatchRateCounting.ClientCoolOff - TimeSpan.FromMinutes(1));
        Assert.Equal(fresh, tooSoon.SelectedDownloadClient!.DownloadClientId);

        var due = await PlanWithLastSend(DispatchRateCounting.ClientCoolOff);
        Assert.Equal(failing, due.SelectedDownloadClient!.DownloadClientId);
        Assert.Contains("trial send", due.ClientChoiceReason, StringComparison.Ordinal);
        Assert.Contains("2 of its last 10 downloads imported (counting the last 30 days)", due.ClientChoiceReason, StringComparison.Ordinal);
    }

    // ── helpers ──

    private static MediaSearchPlan OnePreferred()
    {
        var wire = Candidate("The.Wire.S01E11.1080p.WEBRip.H264-TARS", "WEB 1080p");
        Assert.Equal(ReleaseDecisionStatuses.Preferred, wire.DecisionStatus);
        return new MediaSearchPlan(wire, [wire], "Best feed candidate is The Wire.");
    }

    private static IReadOnlyList<LibraryDownloadClientLinkItem> Clients(params (string Id, int Priority)[] clients)
        => clients.Select(client => new LibraryDownloadClientLinkItem(
            "link-" + client.Id, "library", client.Id, client.Id, client.Priority, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)).ToArray();

    private static AcquisitionDecisionRequest Request(IReadOnlyList<LibraryDownloadClientLinkItem>? clients = null)
        => new(
            "The Wire", 2002, "tv", null, "WEB 1080p",
            Sources: [new LibrarySourceLinkItem("source-link", "library", "idx", "Indexer", 10, "", "", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            DownloadClients: clients ?? Clients(("only", 10)),
            SeasonNumber: 1,
            EpisodeNumber: 11);

    private sealed class FixedPlanner(MediaSearchPlan plan) : IMediaSearchPlanner
    {
        public Task<MediaSearchPlan> BuildPlanAsync(
            string title,
            int? year,
            string mediaType,
            string? currentQuality,
            string? targetQuality,
            IReadOnlyList<LibrarySourceLinkItem> sources,
            IReadOnlyList<CustomFormatItem>? customFormats = null,
            int? seasonNumber = null,
            int? episodeNumber = null,
            IReadOnlyList<string>? allowedQualities = null,
            CancellationToken cancellationToken = default,
            IReadOnlyList<string>? tagNames = null,
            string searchKind = AcquisitionSearchKinds.Automatic,
            DateTimeOffset? availableUtc = null,
            int? currentCustomFormatScore = null,
            string? currentReleaseName = null,
            bool upgradeUntilCutoff = true,
            string? numberingScheme = null,
            int? absoluteNumber = null,
            DateOnly? airDate = null,
            int? sceneSeasonNumber = null,
            int? sceneEpisodeNumber = null,
            PreferenceEvaluationSnapshot? currentPreferenceEvaluation = null,
            ReleasePreferencePlan? preferencePlan = null,
            bool currentFilePresent = false,
            IReadOnlyList<ProfileSizeRule>? sizeRules = null,
            QualityUpgradeStopPolicy? upgradeStop = null,
            ProfileAcquisitionRules? profileAcquisition = null,
            int? runtimeMinutes = null,
            string? imdbId = null,
            ProfileScoreLimits? scoreLimits = null,
            string? genres = null,
            IReadOnlyList<string>? otherTitles = null,
            IReadOnlyList<int>? categoryIds = null,
            IReadOnlyDictionary<int, string>? episodeTitles = null,
            bool queryIsATitle = true,
            IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>? episodeTitlesBySeason = null,
            IReadOnlyCollection<string>? patientIndexerIds = null)
            => Task.FromResult(plan);
    }
}
