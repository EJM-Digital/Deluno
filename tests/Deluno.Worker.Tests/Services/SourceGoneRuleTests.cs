using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1186: a processor's "the download was gone" report asks nothing of anyone when Deluno removed that download itself,
/// closed it, or the title already has the file; otherwise a person still has a question to answer.
/// </summary>
public sealed class SourceGoneRuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    private static ProcessorHandoffItem Handoff() => new(
        "h1", "lib", "movie", "client-1", "queue-1", "Heat.1995.1080p", "/downloads/heat", "Weir", ProcessorHandoffStatuses.Stalled,
        null, null, null, Now, Now);

    private static DownloadDispatchItem Dispatch(string status = "completed", string entityType = "movie", string entityId = "m1", string? importStatus = null, string? importFailureCode = null, DateTimeOffset? created = null, string release = "Heat.1995.1080p", string client = "client-1")
        => new(
            "d-" + Guid.NewGuid().ToString("N"), "lib", "movies", entityType, entityId, release, "Indexer", client, "Client", status, null, created ?? Now.AddHours(-1),
            null, null, null, null, null, null, null, null, null, importStatus, null, null, null, importFailureCode, null, null);

    private sealed class Rig
    {
        public Mock<IDownloadDispatchesRepository> Dispatches { get; } = new();
        public Mock<IMovieCatalogRepository> Movies { get; } = new();
        public Mock<ISeriesCatalogRepository> Series { get; } = new();

        public Rig(params DownloadDispatchItem[] byClientItemId)
        {
            Dispatches.Setup(d => d.FindDispatchesByClientItemIdsAsync("client-1", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(byClientItemId);
            Dispatches.Setup(d => d.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((IReadOnlyList<DownloadDispatchItem>)[], (string?)null));
        }

        public Task<string?> SettledAsync() => SourceGoneRule.SettledBecauseAsync(Handoff(), Dispatches.Object, Movies.Object, Series.Object, CancellationToken.None);
    }

    private static TitleFileHolds Holds(string entityId, DateTimeOffset? arrived) => new(new Dictionary<string, DateTimeOffset?> { [entityId] = arrived });

    [Fact]
    public async Task A_download_Deluno_never_sent_asks_a_person()
    {
        Assert.Null(await new Rig().SettledAsync());
    }

    [Theory]
    [InlineData("imported", null)]
    [InlineData("IMPORTED", null)]
    [InlineData(null, "health-remediation-applied")]
    [InlineData(null, "HEALTH-REMEDIATION-APPLIED")]
    public async Task A_download_Deluno_imported_or_removed_by_its_own_rules_needs_nobody(string? importStatus, string? failureCode)
    {
        var rig = new Rig(Dispatch(importStatus: importStatus, importFailureCode: failureCode));

        Assert.Equal("Deluno removed that download itself, so there is nothing to do.", await rig.SettledAsync());
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("ARCHIVED")]
    public async Task A_download_Deluno_had_already_closed_needs_nobody(string status)
    {
        var rig = new Rig(Dispatch(status: status));

        Assert.Equal("Deluno had already closed that download, so there is nothing to do.", await rig.SettledAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_download_with_no_title_cannot_be_matched_to_a_file_and_asks_a_person(string entityId)
    {
        var rig = new Rig(Dispatch(entityId: entityId));

        Assert.Null(await rig.SettledAsync());
        rig.Movies.VerifyNoOtherCalls();
        rig.Series.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("MOVIE")]
    public async Task A_movie_that_is_in_the_library_already_needs_nobody(string entityType)
    {
        var rig = new Rig(Dispatch(entityType: entityType));
        rig.Movies.Setup(m => m.GetTitleFileHoldsAsync(It.Is<IReadOnlyCollection<string>>(ids => ids.Single() == "m1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("m1", Now.AddMinutes(5)));

        Assert.Equal("The title is in the library already, so there is nothing to do.", await rig.SettledAsync());
        rig.Series.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_episode_that_is_in_the_library_already_is_looked_up_in_the_series_catalogue()
    {
        var rig = new Rig(Dispatch(entityType: "episode", entityId: "e1"));
        rig.Series.Setup(s => s.GetTitleFileHoldsAsync(It.Is<IReadOnlyCollection<string>>(ids => ids.Single() == "e1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("e1", Now.AddMinutes(5)));

        Assert.Equal("The title is in the library already, so there is nothing to do.", await rig.SettledAsync());
        rig.Movies.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_file_that_was_there_before_the_hand_off_began_is_not_the_one_this_download_was_for()
    {
        var rig = new Rig(Dispatch());
        rig.Movies.Setup(m => m.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("m1", Now.AddMinutes(-5)));

        Assert.Null(await rig.SettledAsync());
    }

    [Fact]
    public async Task A_file_with_no_date_counts_as_having_arrived_and_one_for_another_title_does_not_count()
    {
        var undated = new Rig(Dispatch());
        undated.Movies.Setup(m => m.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Holds("m1", null));
        var other = new Rig(Dispatch());
        other.Movies.Setup(m => m.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Holds("m2", Now.AddMinutes(5)));

        Assert.Equal("The title is in the library already, so there is nothing to do.", await undated.SettledAsync());
        Assert.Null(await other.SettledAsync());
    }

    [Fact]
    public async Task A_title_kind_the_rule_does_not_know_asks_a_person()
    {
        var rig = new Rig(Dispatch(entityType: "album"));

        Assert.Null(await rig.SettledAsync());
        rig.Movies.VerifyNoOtherCalls();
        rig.Series.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_send_is_found_by_release_name_and_client_when_the_client_has_not_given_its_id_yet()
    {
        var rig = new Rig();
        var older = Dispatch(importStatus: "imported", created: Now.AddDays(-2));
        var newer = Dispatch(status: "archived", created: Now.AddDays(-1));
        rig.Dispatches.Setup(d => d.QueryDispatchesAsync(
                It.Is<DispatchQueryFilter>(f => f.ClientId == "client-1" && f.IncludeArchived && f.OmitLargeJson),
                It.Is<DispatchPaginationOptions>(p => p.PageSize == 200),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<DownloadDispatchItem>)[
                older,
                newer,
                Dispatch(release: "Other.Release", created: Now),
                Dispatch(client: "client-2", created: Now),
                Dispatch(release: "HEAT.1995.1080P", status: "completed", created: Now.AddDays(-3), importStatus: "imported")
            ], (string?)null));

        var dispatch = await SourceGoneRule.DispatchOfAsync(Handoff(), rig.Dispatches.Object, CancellationToken.None);

        Assert.Same(newer, dispatch);
    }

    [Fact]
    public async Task The_send_with_the_clients_id_is_used_and_the_latest_of_all_the_candidates_wins()
    {
        var byId = Dispatch(created: Now.AddHours(-1));
        var rig = new Rig(byId, Dispatch(created: Now.AddHours(-9)));
        rig.Dispatches.Setup(d => d.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<DownloadDispatchItem>)[Dispatch(created: Now.AddHours(-5))], (string?)null));

        var dispatch = await SourceGoneRule.DispatchOfAsync(Handoff(), rig.Dispatches.Object, CancellationToken.None);

        Assert.Same(byId, dispatch);
        rig.Dispatches.Verify(d => d.FindAnyDispatchByHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task With_no_other_candidate_the_send_is_looked_for_by_the_hash_and_a_missing_page_reads_as_empty()
    {
        var rig = new Rig();
        rig.Dispatches.Setup(d => d.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<DownloadDispatchItem>)null!, (string?)null));
        var byHash = Dispatch();
        rig.Dispatches.Setup(d => d.FindAnyDispatchByHashAsync("client-1", "queue-1", It.IsAny<CancellationToken>())).ReturnsAsync(byHash);

        Assert.Same(byHash, await SourceGoneRule.DispatchOfAsync(Handoff(), rig.Dispatches.Object, CancellationToken.None));
    }

    [Fact]
    public async Task A_client_that_has_never_heard_of_the_download_gives_no_send()
    {
        var rig = new Rig();
        rig.Dispatches.Setup(d => d.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DownloadDispatchItem>)null!);

        Assert.Null(await SourceGoneRule.DispatchOfAsync(Handoff(), rig.Dispatches.Object, CancellationToken.None));
    }
}
