using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>The plain words a person reads on System, Health for four checks: what is wrong, why, and what to do about it.</summary>
public sealed class SecurityAndMetadataHealthChecksWordsTests
{
    [Fact]
    public async Task Allowed_Hosts_off_says_why_and_where_to_fix_it()
    {
        var repository = new Mock<IAllowedHostsRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AllowedHostsSettings.Off);

        var outcome = Assert.Single(await new AllowedHostsHealthCheck(repository.Object).RunAsync(CancellationToken.None));

        Assert.Equal("Allowed Hosts", outcome.Name);
        Assert.Equal("Deluno answers on any address. Choose which ones it should use.", outcome.Message);
        Assert.Equal("Allowed Hosts is off, so Deluno accepts a request no matter which hostname or IP address it was sent to.", outcome.Cause);
        Assert.Equal("Open Security and tick the addresses Deluno should answer on.", outcome.Fix);
    }

    [Fact]
    public async Task Skipping_sign_in_with_Allowed_Hosts_off_says_why_and_where_to_fix_it()
    {
        var local = new Mock<ILocalNetworkAuthRepository>();
        local.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new LocalNetworkAuthSettings(true, [], [], []));
        var hosts = new Mock<IAllowedHostsRepository>();
        hosts.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AllowedHostsSettings.Off);

        var outcome = Assert.Single(await new LocalNetworkAuthHealthCheck(local.Object, hosts.Object).RunAsync(CancellationToken.None));

        Assert.Equal("Skip sign-in for local devices", outcome.Name);
        Assert.Equal("Devices on your local network skip sign-in, and Allowed Hosts is off.", outcome.Message);
        Assert.Equal("With Allowed Hosts off, Deluno answers a request no matter which address it was sent to -- combined with this setting, a device that can reach it on a local address never needs the password.", outcome.Cause);
        Assert.Equal("Open Security and turn on Allowed Hosts, or turn this off.", outcome.Fix);
    }

    [Fact]
    public async Task An_unreachable_metadata_provider_says_why_and_what_to_check()
    {
        var failure = IntegrationFailureFactory.FromLegacy(
            "metadata", "metadata:tmdb:reachability", "TMDb", "metadata.tmdb.reachability", "connectivity", "Timed out.");
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(instance => instance.CheckReachabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderStatus("tmdb", true, "direct", "ready", [], failure));

        var outcome = Assert.Single(await new MetadataProviderHealthCheck(provider.Object).RunAsync(CancellationToken.None));

        Assert.Equal("Metadata (TMDb)", outcome.Name);
        Assert.Equal("TMDb, or the metadata broker, did not answer Deluno's reachability check.", outcome.Cause);
        Assert.Equal("Check the connection from this server to the internet, and confirm the key or broker URL in Settings → Metadata is still correct.", outcome.Fix);
    }

    [Fact]
    public async Task A_held_hand_off_says_why_it_should_not_have_happened()
    {
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var repository = new Mock<IProcessorRepository>();
        repository
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorHandoffItem("h1", "lib", "movie", "c1", "q1", "Sintel.2010", "/d", "Weir", ProcessorHandoffStatuses.Stalled, null, null, null, now, now)]);

        var outcome = Assert.Single(await new ProcessorHandoffHoldHealthCheck(repository.Object).RunAsync(CancellationToken.None));

        Assert.Equal("This shouldn't happen: a hand-off Deluno stopped waiting on, still waiting for you.", outcome.Cause);
    }
}
