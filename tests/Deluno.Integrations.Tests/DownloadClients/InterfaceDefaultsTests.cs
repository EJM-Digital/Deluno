using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// What a download client, its telemetry service and a metadata provider answer when they do not
/// override an optional member: the plain "not supported" answers, and the narrower calls that fall
/// back to the wider one.
/// </summary>
public sealed class InterfaceDefaultsTests
{
    private static DownloadClientItem Client()
        => new(
            "client-1", "SABnzbd", "sabnzbd", "localhost", 8080, null, "api-key", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_client_that_does_not_override_the_optional_members_says_it_cannot()
    {
        var client = new Mock<IDownloadClient> { CallBase = true }.Object;
        var item = Client();

        Assert.False(client.SetsSaveFolderPerGrab);
        Assert.Null(await client.HasUsenetProviderAsync(item, CancellationToken.None));
        Assert.Null(await client.SetUsenetProviderAsync(item, null!, CancellationToken.None));

        var category = await client.SetCategoryAsync(item, "q-1", "movies", null, CancellationToken.None);
        Assert.False(category.Succeeded);
        Assert.Equal("q-1", category.QueueItemId);
        Assert.Equal("sabnzbd does not support this action.", category.Message);

        var check = await client.CheckKeepSeedingCategoryAsync(item, "keep", CancellationToken.None);
        Assert.False(check.Supported);
        Assert.False(check.Found);
        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, check.Status);
        Assert.Equal("SABnzbd is a Usenet client, and only a torrent can keep seeding.", check.Message);

        Assert.False(await client.ClearWarningsAsync(item, CancellationToken.None));
        Assert.Equal(ClientResumeResult.NotSupported, await client.ResumeClientAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task A_telemetry_service_falls_back_to_the_plain_overview_and_refuses_what_it_cannot_do()
    {
        var overview = new DownloadTelemetryOverview(null!, [], DateTimeOffset.Parse("2026-10-31T00:00:00Z"));
        IReadOnlyList<DownloadClientTelemetrySnapshot> snapshots = [];
        var mock = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        mock.Setup(s => s.GetOverviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync(overview);
        mock.Setup(s => s.GetSnapshotsForLeftoverScanAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshots);
        mock.Setup(s => s.ExecuteActionAsync("c", It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("c", "q", "pause", true, "ok"));
        var service = mock.Object;

        var capture = await service.CaptureAsync(CancellationToken.None);
        Assert.Same(overview, capture.Overview);
        Assert.Same(snapshots, capture.LeftoverSnapshots);
        Assert.Same(overview, await service.GetLatestOverviewAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Same(overview, await service.GetFreshOverviewAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Same(snapshots, await service.GetFreshSnapshotsForLeftoverScanAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        var viaDispatch = await service.ExecuteActionForDispatchAsync("c", new DownloadClientActionRequest("pause", "q"), null!, CancellationToken.None);
        Assert.True(viaDispatch.Succeeded);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.RemoveWithOptionsAsync("c", "q", true, true, false, CancellationToken.None));

        var ownership = await service.CheckOwnershipAsync("c", "q", CancellationToken.None);
        Assert.True(ownership.Owned);
        Assert.Null(ownership.Message);

        var resume = await service.ResumeClientAsync("c", CancellationToken.None);
        Assert.False(resume.Succeeded);
        Assert.Equal(("c", "", "resume-client", "Resuming a whole client is not available here."),
            (resume.ClientId, resume.QueueItemId, resume.Action, resume.Message));
    }

    [Fact]
    public async Task A_metadata_provider_that_does_not_override_the_optional_members_does_nothing_or_falls_back()
    {
        IReadOnlyList<MetadataEpisodeGroup> groups = [];
        var mock = new Mock<IMetadataProvider> { CallBase = true };
        mock.Setup(p => p.GetSeriesEpisodeGroupsAsync("tv-1", It.IsAny<CancellationToken>())).ReturnsAsync(groups);
        var provider = mock.Object;

        Assert.Same(NoMetadataFailureScope.Instance, provider.BeginFailureScope());
        await provider.ClearServiceTroubleAsync(CancellationToken.None);
        Assert.Same(groups, await provider.GetSeriesEpisodeGroupsAsync("tv-1", MetadataCatalogueSource.Preferred, CancellationToken.None));
        Assert.Empty(await provider.GetDiscoverListAsync("movie", new DiscoverListQuery(DiscoverListQuery.Popular, 1), CancellationToken.None));
    }
}
