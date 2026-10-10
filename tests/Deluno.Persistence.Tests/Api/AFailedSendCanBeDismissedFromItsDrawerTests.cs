using Deluno.Jobs;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1218: Activity's Failed count held sends that were refused or failed, and their drawers offered only "Lift the
/// block". Every counted send can now be dismissed: it leaves the Failed count, a send that is not failed has nothing
/// to dismiss, and the block list is not touched.
/// </summary>
public sealed class AFailedSendCanBeDismissedFromItsDrawerTests
{
    [Fact]
    public async Task A_failed_send_is_dismissed_and_leaves_the_failed_state()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await RecordSendAsync(app, "Brokenfilm.2024.1080p");
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.RecordImportOutcomeAsync(id, "failed", null, "refused", "The file check refused it.", CancellationToken.None);
        Assert.True(DispatchFailure.IsFailed((await dispatches.GetDispatchAsync(id, CancellationToken.None))!));

        var response = await app.Client.PostAsJsonAsync($"/api/download-dispatches/{id}/dismiss", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var after = await dispatches.GetDispatchIncludingArchivedAsync(id, CancellationToken.None);
        Assert.False(DispatchFailure.IsFailed(after!));
        Assert.Equal("archived", after!.Status);
    }

    [Fact]
    public async Task A_send_that_has_not_failed_has_nothing_to_dismiss()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await RecordSendAsync(app, "Fine.Film.2024.1080p");

        var response = await app.Client.PostAsJsonAsync($"/api/download-dispatches/{id}/dismiss", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchAsync(id, CancellationToken.None))!.Status == "archived");
    }

    [Fact]
    public async Task A_send_that_does_not_exist_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/download-dispatches/nope/dismiss", new { })).StatusCode);
    }

    private static Task<string> RecordSendAsync(ApplicationTestHost app, string release)
        => app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            "lib-1", "movies", "movie", "movie-1", release, "Indexer", "sabnzbd", "SABnzbd", "queued", null,
            cancellationToken: CancellationToken.None);
}
