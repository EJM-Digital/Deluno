using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>Media servers, subtitle providers and Seerr read their stored verdict onto the health board (#909).</summary>
public sealed class ExternalServiceHealthCheckTests
{
    private static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static MediaServerConnection Server(string id, string name, bool enabled, string status, string? message = null)
        => Blank<MediaServerConnection>() with { Id = id, Name = name, IsEnabled = enabled, HealthStatus = status, LastHealthMessage = message };

    private static SubtitleProviderConnection Provider(string id, string name, bool enabled, string status, string? message = null)
        => Blank<SubtitleProviderConnection>() with { Id = id, Name = name, IsEnabled = enabled, HealthStatus = status, LastHealthMessage = message };

    private static SeerrConnection Seerr(string id, string name, bool enabled, string status, string? message = null)
        => Blank<SeerrConnection>() with { Id = id, Name = name, IsEnabled = enabled, HealthStatus = status, LastHealthMessage = message };

    private static ExternalServiceHealthCheck Check(MediaServerConnection[] servers, SubtitleProviderConnection[] providers, SeerrConnection[] seerr)
    {
        var serverRepo = new Mock<IMediaServerRepository>();
        serverRepo.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(servers);
        var providerRepo = new Mock<ISubtitleProviderRepository>();
        providerRepo.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(providers);
        var seerrRepo = new Mock<ISeerrRepository>();
        seerrRepo.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(seerr);
        return new ExternalServiceHealthCheck(serverRepo.Object, providerRepo.Object, seerrRepo.Object);
    }

    [Fact]
    public async Task Each_enabled_media_server_subtitle_provider_and_Seerr_gets_a_row_from_its_stored_verdict_and_a_disabled_one_gets_none()
    {
        var outcomes = await Check(
            [Server("s1", "Plex Den", true, "healthy"), Server("s2", "Old Emby", false, "unreachable")],
            [Provider("p1", "OpenSubs", true, "unreachable", "HTTP 503"), Provider("p2", "Idle Subs", false, "unreachable")],
            [Seerr("q1", "Seerr", true, "healthy")]).RunAsync(CancellationToken.None);

        Assert.Equal(["media-server:s1", "subtitle-provider:p1", "seerr:q1"], outcomes.Select(outcome => outcome.Id));
        Assert.Equal(HealthCheckStatuses.Ok, outcomes[0].Status);
        Assert.Equal("Plex Den is reachable.", outcomes[0].Message);
        Assert.Equal(HealthCheckCategories.MediaServer, outcomes[0].Category);

        Assert.Equal(HealthCheckStatuses.Problem, outcomes[1].Status);
        Assert.Equal("OpenSubs: HTTP 503", outcomes[1].Message);
        Assert.Equal("Open Connections and test OpenSubs.", outcomes[1].Fix);
        Assert.Equal(HealthCheckCategories.SubtitleProvider, outcomes[1].Category);

        Assert.Equal(HealthCheckStatuses.Ok, outcomes[2].Status);
        Assert.Equal(HealthCheckCategories.Seerr, outcomes[2].Category);
    }

    [Fact]
    public async Task A_server_that_is_not_healthy_and_left_no_message_says_it_is_not_answering()
    {
        var outcome = Assert.Single(await Check([], [], [Seerr("q1", "Seerr", true, "unreachable")]).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Seerr is not answering.", outcome.Message);
    }

    [Fact]
    public async Task With_nothing_connected_there_are_no_rows()
    {
        Assert.Empty(await Check([], [], []).RunAsync(CancellationToken.None));
    }
}
