using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Moq;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// The wanted/archived/satisfied verdict for one episode and the quality gap to a candidate,
/// for every branch: no show, no episode, each state of file, monitoring and cutoff.
/// </summary>
public sealed class EpisodeWorkflowServiceTests
{
    private static SeriesEpisodeInventoryItem Episode(bool monitored, bool hasFile, bool cutoffMet)
        => new(
            "e-1", 1, 1, "Pilot", null, null, monitored, hasFile,
            "missing", "reason", cutoffMet, null, null, false, null, null, null,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static EpisodeWorkflowService Service(SeriesInventoryDetail? inventory, out Mock<ISeriesCatalogRepository> repository, Mock<IVersionedMediaPolicyEngine>? engine = null)
    {
        repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(r => r.GetInventoryDetailAsync("s-1", It.IsAny<CancellationToken>())).ReturnsAsync(inventory);
        return new EpisodeWorkflowService(repository.Object, (engine ?? new Mock<IVersionedMediaPolicyEngine>()).Object);
    }

    private static SeriesInventoryDetail Detail(params SeriesEpisodeInventoryItem[] episodes)
        => new("s-1", "Show", 2020, 1, episodes.Length, 0, episodes);

    [Fact]
    public async Task A_show_that_is_not_there_is_unknown()
    {
        var decision = await Service(null, out _).EvaluateEpisodeAsync("e-1", "s-1", "lib", CancellationToken.None);
        Assert.Equal("unknown", decision.Decision);
        Assert.Equal("Series not found", decision.Reason);
        Assert.Equal("e-1", decision.EpisodeId);
    }

    [Fact]
    public async Task An_episode_that_is_not_in_the_show_is_unknown()
    {
        var decision = await Service(Detail(), out _).EvaluateEpisodeAsync("e-1", "s-1", "lib", CancellationToken.None);
        Assert.Equal("unknown", decision.Decision);
        Assert.Equal("Episode not found", decision.Reason);
    }

    [Theory]
    [InlineData(true, true, true, "archived", "Has file and quality cutoff met")]
    [InlineData(false, true, true, "archived", "Has file and quality cutoff met")]
    [InlineData(true, false, false, "wanted", "Monitored but missing")]
    [InlineData(true, true, false, "wanted", "Has file but quality below cutoff")]
    [InlineData(false, false, false, "satisfied", "No action needed")]
    [InlineData(false, true, false, "satisfied", "No action needed")]
    public async Task Each_state_of_file_monitoring_and_cutoff_gets_its_verdict(bool monitored, bool hasFile, bool cutoffMet, string decision, string reason)
    {
        var result = await Service(Detail(Episode(monitored, hasFile, cutoffMet)), out _).EvaluateEpisodeAsync("e-1", "s-1", "lib", CancellationToken.None);
        Assert.Equal(decision, result.Decision);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData(null, "1080p")]
    [InlineData("", "1080p")]
    [InlineData("720p", null)]
    [InlineData("720p", " ")]
    public async Task No_gap_is_given_when_either_quality_is_missing(string? current, string? candidate)
    {
        var service = Service(null, out var repository);
        repository.Setup(r => r.GetEpisodeCurrentQualityAsync("e-1", It.IsAny<CancellationToken>())).ReturnsAsync(current);
        Assert.Null(await service.CalculateEpisodeQualityDeltaAsync("e-1", "lib", candidate!, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(3, -1)]
    public async Task No_gap_is_given_when_a_quality_has_no_rank(int currentRank, int candidateRank)
    {
        var engine = new Mock<IVersionedMediaPolicyEngine>();
        engine.Setup(e => e.QualityRank("720p")).Returns(currentRank);
        engine.Setup(e => e.QualityRank("1080p")).Returns(candidateRank);
        var service = Service(null, out var repository, engine);
        repository.Setup(r => r.GetEpisodeCurrentQualityAsync("e-1", It.IsAny<CancellationToken>())).ReturnsAsync("720p");
        Assert.Null(await service.CalculateEpisodeQualityDeltaAsync("e-1", "lib", "1080p", CancellationToken.None));
    }

    [Fact]
    public async Task The_gap_is_the_candidate_rank_minus_the_current_rank()
    {
        var engine = new Mock<IVersionedMediaPolicyEngine>();
        engine.Setup(e => e.QualityRank("720p")).Returns(2);
        engine.Setup(e => e.QualityRank("1080p")).Returns(5);
        var service = Service(null, out var repository, engine);
        repository.Setup(r => r.GetEpisodeCurrentQualityAsync("e-1", It.IsAny<CancellationToken>())).ReturnsAsync("720p");
        Assert.Equal(3, await service.CalculateEpisodeQualityDeltaAsync("e-1", "lib", "1080p", CancellationToken.None));
    }
}
