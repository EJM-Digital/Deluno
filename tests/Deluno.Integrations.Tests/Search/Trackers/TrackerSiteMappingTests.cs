using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #986: seven of Deluno's nine hand-written sites were dropped for the
/// built-in Prowlarr list; an indexer saved against one of them keeps working
/// from the list's file of the same site with its settings kept, one Activity
/// line says so, and one that cannot be matched is left untouched and shown on
/// Health. Nothing is ever deleted.
/// </summary>
public sealed class TrackerSiteMappingTests : IDisposable
{
    private static readonly string[] RetiredIds = ["yts", "thepiratebay", "limetorrents", "1337x", "nyaasi", "rutor", "internetarchive"];

    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "deluno-site-mapping-" + Guid.NewGuid().ToString("n"));
    private readonly TrackerDefinitionCatalog catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static IndexerItem Indexer(string id, string name, string? definitionId, Dictionary<string, string>? settings = null, Dictionary<string, string>? secrets = null)
        => new(id, name, "tracker", "public", "https://old.example/", null, 10, "2000", string.Empty, "both", true, "healthy", null, null, null, null, 0, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            DefinitionId = definitionId,
            DefinitionSettings = settings,
            DefinitionSecrets = secrets
        };

    [Fact]
    public void No_hand_written_site_that_the_list_covers_is_still_built_in()
    {
        var kept = TrackerDefinitionCatalog.BuiltIn().Select(item => item.Id).Order().ToArray();

        // EZTV (IMDb-id API) and TorrentDownloads (RSS feed with the info hash) do something the list's own file cannot.
        Assert.Equal(["eztv", "torrentdownloads"], kept);
        Assert.Empty(RetiredIds.Intersect(kept));
        Assert.Equal(RetiredIds.Order(), TrackerSiteMapping.Retired.Keys.Order());
    }

    [Fact]
    public void Each_retired_site_maps_to_the_list_file_of_the_same_site_and_keeps_its_settings()
    {
        foreach (var id in RetiredIds)
        {
            var definition = catalog.Find(id);
            Assert.NotNull(definition);
            Assert.Equal(TrackerDefinitionOrigins.Bundled, catalog.List().Single(item => item.Id == id).Origin);

            // A setting the site's list file really has, saved earlier, is kept.
            var real = definition.Settings.FirstOrDefault(setting => !setting.IsInformational && !setting.IsSecret);
            var settings = real is null ? null : new Dictionary<string, string> { [real.Name] = real.Default ?? "x" };
            var resolution = TrackerSiteMapping.Resolve(Indexer("i-" + id, id, id, settings), catalog);

            Assert.True(resolution.WasOwnSite, id);
            Assert.Equal(id, resolution.SiteId);
            Assert.Null(resolution.Problem);
        }
    }

    [Fact]
    public void A_site_the_list_does_not_have_or_a_setting_it_does_not_know_is_a_problem_in_plain_words()
    {
        var gone = TrackerSiteMapping.Resolve(Indexer("1", "Gone Tracker", "no-such-site"), catalog);
        Assert.NotNull(gone.Problem);
        Assert.Contains("no-such-site", gone.Problem, StringComparison.Ordinal);

        var lost = TrackerSiteMapping.Resolve(Indexer("2", "My YTS", "yts", new() { ["retired_option"] = "1" }), catalog);
        Assert.True(lost.WasOwnSite);
        Assert.Contains("retired_option", lost.Problem, StringComparison.Ordinal);

        var none = TrackerSiteMapping.Resolve(Indexer("3", "Blank", null), catalog);
        Assert.NotNull(none.Problem);
    }

    [Fact]
    public async Task Health_shows_only_the_indexers_that_cannot_be_matched_and_deletes_nothing()
    {
        var repository = new Mock<IConnectionsRepository>(MockBehavior.Strict);
        repository.Setup(item => item.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Indexer("a", "Fine YTS", "yts"),
            Indexer("b", "Gone Tracker", "no-such-site"),
            Indexer("c", "Old Nyaa", "nyaasi", new() { ["retired_option"] = "1" }),
            Indexer("d", "Newznab", null) with { Protocol = "newznab" }
        ]);

        var outcomes = await new TrackerSiteHealthCheck(repository.Object, catalog).RunAsync(CancellationToken.None);

        Assert.Equal(["tracker-site:b", "tracker-site:c"], outcomes.Select(item => item.Id).Order());
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
            Assert.Contains("Nothing was deleted", outcome.Fix, StringComparison.Ordinal);
        });
        // Strict mock: any delete or update call would have thrown.
        repository.Verify(item => item.ListIndexersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task One_activity_line_names_the_indexers_that_moved_and_it_is_said_once()
    {
        var repository = new Mock<IConnectionsRepository>(MockBehavior.Strict);
        repository.Setup(item => item.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Indexer("a", "My YTS", "yts"),
            Indexer("b", "Old Nyaa", "nyaasi"),
            Indexer("c", "EZTV", "eztv"),
            Indexer("d", "Gone Tracker", "no-such-site"),
            Indexer("e", "Odd Rutor", "rutor", new() { ["retired_option"] = "1" })
        ]);
        var activity = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var lines = new List<(string Category, string Message)>();
        activity.Setup(item => item.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((category, message, _, _, _, _, _) => lines.Add((category, message)))
            .ReturnsAsync((ActivityEventItem)null!);
        var service = new TrackerSiteSwitchHostedService(
            repository.Object, catalog, activity.Object, Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            Mock.Of<IDelunoStartupGate>(), NullLogger<TrackerSiteSwitchHostedService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);

        var line = Assert.Single(lines);
        Assert.Equal("trackers.definitions.switch", line.Category);
        Assert.Contains("My YTS", line.Message, StringComparison.Ordinal);
        Assert.Contains("Old Nyaa", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Gone Tracker", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Odd Rutor", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("EZTV", line.Message, StringComparison.Ordinal);
        Assert.Contains("settings were kept", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_install_with_no_moved_indexers_says_nothing()
    {
        var repository = new Mock<IConnectionsRepository>(MockBehavior.Strict);
        repository.Setup(item => item.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Indexer("c", "EZTV", "eztv")]);
        var activity = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var service = new TrackerSiteSwitchHostedService(
            repository.Object, catalog, activity.Object, Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            Mock.Of<IDelunoStartupGate>(), NullLogger<TrackerSiteSwitchHostedService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);

        activity.VerifyNoOtherCalls();
    }
}
