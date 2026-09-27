using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks;

/// <summary>
/// TMDb (and the metadata broker) must be checked on the fifteen-minute
/// health schedule, not only when a person opens Settings and presses Test
/// (#909). This exercises what the check does with whatever
/// <see cref="IMetadataProvider.CheckReachabilityAsync"/> -- the active probe
/// -- comes back with, not the provider itself.
/// </summary>
public sealed class MetadataProviderHealthCheckTests
{
    [Fact]
    public async Task Unconfigured_metadata_reports_nothing()
    {
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(instance => instance.CheckReachabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderStatus("tmdb", false, "unconfigured", "Not set up yet.", []));

        var outcomes = await new MetadataProviderHealthCheck(provider.Object).RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_reachable_provider_reports_ok()
    {
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(instance => instance.CheckReachabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderStatus("tmdb", true, "direct", "Deluno's title-matching service is ready.", []));

        var outcomes = await new MetadataProviderHealthCheck(provider.Object).RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal(HealthCheckCategories.Metadata, outcome.Category);
    }

    [Fact]
    public async Task An_unreachable_provider_is_reported_as_a_problem_with_a_fix()
    {
        var failure = IntegrationFailureFactory.FromLegacy(
            "metadata", "metadata:tmdb:reachability", "TMDb", "metadata.tmdb.reachability", "connectivity",
            "The service returned HTTP 503.");
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(instance => instance.CheckReachabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderStatus("tmdb", true, "direct", "Deluno's title-matching service is ready.", [], failure));

        var outcomes = await new MetadataProviderHealthCheck(provider.Object).RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("TMDb", outcome.Message);
        Assert.NotNull(outcome.Fix);
    }
}
