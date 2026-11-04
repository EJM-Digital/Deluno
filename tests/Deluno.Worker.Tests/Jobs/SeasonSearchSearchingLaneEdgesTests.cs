using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Services;
using Deluno.Worker.Jobs;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#1442: the edges of the card a season search puts on Live: Specials, a job that names no show, and a handler with no lane to tell.</summary>
public sealed class SeasonSearchSearchingLaneEdgesTests
{
    private readonly List<SearchingTitle> _cards = [];

    private (SeasonSearchJobHandler Handler, List<string> LabelsSeen) Build(SearchesInFlight? searching, List<(string TriggeredBy, string Clause)>? asked = null)
    {
        var labels = new List<string>();
        var executor = new Mock<ISeasonSearchExecutor>();
        executor
            .Setup(item => item.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns((string _, int _, string triggeredBy, string clause, CancellationToken _, string? _) =>
            {
                asked?.Add((triggeredBy, clause));
                if (searching is not null)
                {
                    _cards.AddRange(searching.Snapshot());
                    labels.AddRange(searching.Snapshot().Select(card => card.Label));
                }

                return Task.FromResult(new SeasonSearchOutcome(SeasonSearchStatus.Completed, "matched", null, 1, 1, 0, 0, 1, 1, "Pioneer.One.S00", "Example", "sent", "Sent.", null, [], ["episode-1"], true));
            });
        return (new SeasonSearchJobHandler(executor.Object, new Mock<IJobQueueRepository>().Object, searching), labels);
    }

    private static Deluno.Jobs.Contracts.JobQueueItem Job(string payloadJson)
        => TestJobs.Create("series.search.season", payloadJson: payloadJson, relatedEntityId: "series-1", relatedEntityType: "series");

    private static string Payload(string seriesId, int season) => JsonSerializer.Serialize(new
    {
        seriesId, seriesTitle = "Pioneer One", libraryId = "library-tv", seasonNumber = season,
        episodes = new[] { new { episodeId = "episode-1", episodeNumber = 1, title = "Bought" } }
    });

    [Fact]
    public async Task Season_zero_is_shown_as_Specials()
    {
        var searching = new SearchesInFlight(TimeProvider.System);
        var (handler, labels) = Build(searching);

        await handler.HandleAsync(Job(Payload("series-1", 0)), CancellationToken.None);

        Assert.Equal(["Specials"], labels);
        Assert.Empty(searching.Snapshot());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"seriesId":"  ","seriesTitle":"x","libraryId":"l","seasonNumber":1,"episodes":[]}""")]
    public async Task A_job_that_names_no_show_puts_nothing_on_the_lane_and_still_runs(string payloadJson)
    {
        var searching = new SearchesInFlight(TimeProvider.System);
        var (handler, labels) = Build(searching);

        var message = await handler.HandleAsync(Job(payloadJson), CancellationToken.None);

        Assert.Equal("Finished searching for season.", message);

        Assert.Empty(labels);
        Assert.Empty(searching.Snapshot());
    }

    [Fact]
    public async Task The_search_is_asked_for_as_the_automatic_season_search_in_its_own_words_and_the_card_names_the_show()
    {
        var searching = new SearchesInFlight(TimeProvider.System);
        var asked = new List<(string TriggeredBy, string Clause)>();
        var (handler, _) = Build(searching, asked);

        await handler.HandleAsync(Job(Payload("series-1", 2)), CancellationToken.None);

        Assert.Equal([("automatic-season", "was searched by the automatic library cycle.")], asked);
        var card = Assert.Single(_cards);
        Assert.Equal(("series", "series-1", "Pioneer One", "missing", "Season 2"), (card.EntityType, card.EntityId, card.Title, card.StartedBy, card.Label));
    }

    [Fact]
    public async Task Without_a_lane_to_tell_the_search_just_runs()
    {
        var (handler, _) = Build(null);

        var message = await handler.HandleAsync(Job(Payload("series-1", 2)), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Equal("series.search.season", handler.JobType);
    }

    [Fact]
    public void A_lane_remembers_its_start_delay_and_the_job_types_it_wakes_for()
    {
        var lane = new JobLane("test", TimeSpan.FromSeconds(5), ["a"], JitterOverride: TimeSpan.FromMilliseconds(250), SignalTypesOverride: ["b"]);

        Assert.Equal(TimeSpan.FromMilliseconds(250), lane.JitterOverride);
        Assert.Equal(["b"], lane.SignalTypesOverride);
        Assert.True(lane.Enabled);
        Assert.Null(new JobLane("x", TimeSpan.FromSeconds(1), []).JitterOverride);
    }
}
