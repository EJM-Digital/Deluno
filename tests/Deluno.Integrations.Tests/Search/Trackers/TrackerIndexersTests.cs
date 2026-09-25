using Deluno.Connections.Contracts;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #606: an indexer row whose definition id no longer exists in the catalogue
/// (removed, renamed, or a data folder copied without its trackers directory)
/// must not crash a search, and must show a clear, clearable reason on the
/// Indexers screen and in health rather than a raw exception.
/// </summary>
public sealed class TrackerIndexersTests
{
    private static readonly TrackerDefinitionCatalog Catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    [Fact]
    public void Connecting_an_indexer_whose_definition_is_gone_fails_with_a_plain_english_reason()
    {
        var indexers = new TrackerIndexers(Catalog, engine: null!);
        var indexer = CreateIndexer("indexer-1", "Old Tracker", definitionId: "long-removed-tracker");

        var exception = Assert.Throws<TrackerException>(() => indexers.Connect(indexer));

        Assert.Contains("not installed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trackers folder", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remove this indexer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Testing_an_indexer_whose_definition_is_gone_reports_degraded_health_instead_of_throwing()
    {
        var indexers = new TrackerIndexers(Catalog, engine: null!);
        var indexer = CreateIndexer("indexer-1", "Old Tracker", definitionId: "long-removed-tracker");

        var (status, message, category) = await indexers.TestAsync(indexer, CancellationToken.None);

        Assert.Equal("degraded", status);
        Assert.Equal("configuration", category);
        Assert.Contains("not installed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_indexer_that_never_says_which_tracker_it_is_fails_with_its_own_reason()
    {
        var indexers = new TrackerIndexers(Catalog, engine: null!);
        var indexer = CreateIndexer("indexer-1", "Nameless Tracker", definitionId: null);

        var exception = Assert.Throws<TrackerException>(() => indexers.Connect(indexer));

        Assert.Contains("does not say which tracker it is", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IndexerItem CreateIndexer(string id, string name, string? definitionId)
        => new(
            Id: id,
            Name: name,
            Protocol: "tracker",
            Privacy: "public",
            BaseUrl: string.Empty,
            ApiKey: null,
            Priority: 1,
            Categories: "2000,5000",
            Tags: "",
            MediaScope: "both",
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            ConsecutiveFailures: 0,
            RateLimitedUntilUtc: null,
            DisabledReason: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow)
        {
            DefinitionId = definitionId
        };
}
