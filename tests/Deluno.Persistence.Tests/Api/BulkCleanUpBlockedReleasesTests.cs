using System.Net.Http.Json;
using Deluno.Api.BlockedReleases;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The bulk half of "Clean up now" (#800): the Blocklist screen used to be
/// able to clear one release's client-side traces at a time only. This route
/// runs the identical per-release rules — <see cref="IRefusedDownloadCleanupService"/>,
/// unchanged — across every refused release in one call.
/// </summary>
public sealed class BulkCleanUpBlockedReleasesTests
{
    [Fact]
    public async Task Cleanup_all_reports_nothing_to_clear_when_no_refusal_has_a_client_and_hash()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        // Blocked from the "just block it" endpoint: no download client, no
        // hash, so nothing here is awaiting cleanup at a client.
        await app.Client.PostAsJsonAsync(
            "/api/blocked-releases",
            new BlockReleaseRequest("Arrival.2016.2160p.UHD.BluRay", "Nebula", MediaType: "movie", Title: "Arrival"));

        var response = await app.Client.PostAsync("/api/blocked-releases/cleanup-all", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        var result = await response.Content.ReadFromJsonAsync<RefusedDownloadCleanupResult>();
        Assert.NotNull(result);
        Assert.Equal(0, result!.Cleared);
        Assert.Equal(0, result.WaitingOnSharing);
        Assert.Equal(0, result.Failed);

        // Nothing to clean up, so the block is untouched — still listed,
        // still refused.
        var blocklist = app.Services.GetRequiredService<IBlockedReleaseRepository>();
        Assert.Single(await blocklist.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_all_requires_authentication()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var response = await anonymous.PostAsync("/api/blocked-releases/cleanup-all", null);

        Assert.False(response.IsSuccessStatusCode);
    }
}
