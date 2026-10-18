using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Give up and search for another release" (#914, #929) as a real action: a
/// button and an endpoint, not just words in a recovery case's recommended
/// text. The title comes from the download dispatch the hand-off's own
/// client/queue-item pair names -- the same lookup a Queue "mark as bad" uses
/// -- so the release gets blocklisted (#874: hash/id and name) and the new
/// search lands on the exact title the hand-off was for.
/// </summary>
public sealed class ProcessorHandoffSearchAgainTests
{
    [Theory]
    [InlineData(ProcessorHandoffStatuses.TimedOut)]
    [InlineData(ProcessorHandoffStatuses.Stalled)]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    public async Task Giving_up_blocklists_the_release_ends_the_handoff_and_searches_again(string endedStatus)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffWithDispatchAsync(app, endedStatus, mediaType: "tv", entityType: "series", entityId: "series-1");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var stored = await ProcessorRepository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored?.Status);

        var blocked = await app.Client.GetAsync("/api/blocked-releases");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Contains(
            blockedBody.RootElement.EnumerateArray(),
            item => item.GetProperty("releaseName").GetString() == handoff.ReleaseName);

        var jobs = await JobQueueRepository(app).ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job =>
            job.JobType == LibrarySearchJobTypes.For("tv") &&
            job.PayloadJson != null &&
            job.PayloadJson.Contains("\"targetEntityId\":\"series-1\"", StringComparison.OrdinalIgnoreCase));

        var feed = await app.Client.GetAsync("/api/activity?pageSize=50");
        using var feedBody = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.Contains(
            feedBody.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "processing.search-again");
    }

    /// <summary>
    /// Golden path rc.30.8: the given-up torrent was still in the client, so the new search was skipped as
    /// "already on its way". Giving up now takes that download out of the client first (only one Deluno sent), and
    /// says so; the release is blocklisted and the search is queued.
    /// </summary>
    [Fact]
    public async Task Giving_up_removes_the_given_up_download_from_the_client_blocklists_it_and_searches_now()
    {
        var removal = new RecordingRemoval(removed: true);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddScoped<IGivenUpDownloadRemoval>(_ => removal));
        var handoff = await HandoffWithDispatchAsync(app, ProcessorHandoffStatuses.Failed, mediaType: "tv", entityType: "series", entityId: "series-1");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = body.RootElement.GetProperty("message").GetString();
        Assert.Contains("Removed it from the download client with its files", message);
        Assert.Contains("blocklisted", message);
        var removedDispatch = Assert.Single(removal.Removed);
        Assert.Equal("nzo-1", removedDispatch.TorrentHashOrItemId);
        Assert.Equal("sabnzbd", removedDispatch.DownloadClientId);

        var jobs = await JobQueueRepository(app).ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job => job.JobType == LibrarySearchJobTypes.For("tv") && job.PayloadJson!.Contains("\"targetEntityId\":\"series-1\""));
    }

    /// <summary>
    /// The same give-up against a real qBittorrent 5.2.1 (tests/fixtures/recorded) on a loopback port, with the real removal and no
    /// stand-in for it: the torrent is taken out of the client with its files (the recorded delete call with deleteFiles=true, for
    /// the hash Deluno recorded sending), the release is blocklisted, and one search for the title is queued.
    /// </summary>
    [Fact]
    public async Task Giving_up_on_a_download_in_a_recorded_qbittorrent_removes_it_with_its_files_blocklists_it_and_searches_now()
    {
        const string hash = "69ca35bca234b9cb822abc435ddbababf6147591";
        const string release = "Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "removed-with-data" }.Select(RecordedReplies.Qbittorrent),
            RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0], RecordedReplies.Qbittorrent("removed-with-data").Exchanges[1]));
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var client = await created.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = client.GetProperty("id").GetString()!;
        var dispatchId = await JobQueueRepository(app).RecordDownloadDispatchAsync(
            "library-1", "tv", "series", "series-1", release, "Indexer", clientId, client.GetProperty("name").GetString()!, "queued", null,
            cancellationToken: CancellationToken.None);
        await DispatchesRepository(app).RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: hash);
        var handoff = await ProcessorRepository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("library-1", "tv", clientId, hash, release, @"C:Downloads-CompleteTV\" + release, "Weir"),
            CancellationToken.None);
        await ProcessorRepository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Failed, null, null, "Weir could not read the file.", CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("Removed it from the download client with its files", body.RootElement.GetProperty("message").GetString());
        var delete = Assert.Single(qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));
        Assert.Contains(hash, delete.Body, StringComparison.Ordinal);
        Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);

        var blocked = await app.Client.GetFromJsonAsync<JsonElement>("/api/blocked-releases");
        Assert.Contains(blocked.EnumerateArray(), item => item.GetProperty("releaseName").GetString() == release);
        var jobs = await JobQueueRepository(app).ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job => job.JobType == LibrarySearchJobTypes.For("tv") && job.PayloadJson!.Contains("\"targetEntityId\":\"series-1\""));
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await ProcessorRepository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
    }

    /// <summary>A client that cannot be reached must not stop the release being blocklisted and searched for; the screen says why the removal waits.</summary>
    [Fact]
    public async Task A_removal_that_could_not_be_done_is_said_and_the_search_still_starts()
    {
        var removal = new RecordingRemoval(removed: false);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddScoped<IGivenUpDownloadRemoval>(_ => removal));
        var handoff = await HandoffWithDispatchAsync(app, ProcessorHandoffStatuses.Failed, mediaType: "tv", entityType: "series", entityId: "series-1");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("could not be removed from the download client", body.RootElement.GetProperty("message").GetString());
        var jobs = await JobQueueRepository(app).ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job => job.JobType == LibrarySearchJobTypes.For("tv"));
    }

    private sealed class RecordingRemoval(bool removed) : IGivenUpDownloadRemoval
    {
        public List<Deluno.Jobs.Contracts.DownloadDispatchItem> Removed { get; } = [];

        public Task<GivenUpDownloadRemovalResult> RemoveAsync(Deluno.Jobs.Contracts.DownloadDispatchItem dispatch, CancellationToken cancellationToken)
        {
            Removed.Add(dispatch);
            return Task.FromResult(new GivenUpDownloadRemovalResult(removed, removed ? null : "the client did not answer"));
        }
    }

    /// <summary>
    /// #956: a processor that never received the file has a hand-off problem,
    /// not a bad release (the same rule as #880 and #919). "Search for
    /// another release" on a <see cref="ProcessorHandoffStatuses.Lost"/>
    /// hand-off must still end it and search again, but must not blocklist a
    /// release that was never at fault.
    /// </summary>
    [Fact]
    public async Task Giving_up_on_a_lost_handoff_ends_it_and_searches_again_without_blocklisting()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffWithDispatchAsync(app, ProcessorHandoffStatuses.Lost, mediaType: "tv", entityType: "series", entityId: "series-1");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = responseBody.RootElement.GetProperty("message").GetString();
        Assert.Contains("never received", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not blocklist", message, StringComparison.OrdinalIgnoreCase);

        var stored = await ProcessorRepository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored?.Status);

        var blocked = await app.Client.GetAsync("/api/blocked-releases");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            blockedBody.RootElement.EnumerateArray(),
            item => item.GetProperty("releaseName").GetString() == handoff.ReleaseName);

        var jobs = await JobQueueRepository(app).ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job =>
            job.JobType == LibrarySearchJobTypes.For("tv") &&
            job.PayloadJson != null &&
            job.PayloadJson.Contains("\"targetEntityId\":\"series-1\"", StringComparison.OrdinalIgnoreCase));

        var feed = await app.Client.GetAsync("/api/activity?pageSize=50");
        using var feedBody = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.Contains(
            feedBody.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "processing.search-again");
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    [InlineData(ProcessorHandoffStatuses.Completed)]
    [InlineData(ProcessorHandoffStatuses.Dismissed)]
    public async Task A_handoff_that_is_not_needing_a_person_refuses_the_search(string status)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffWithDispatchAsync(app, status, mediaType: "tv", entityType: "series", entityId: "series-1");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_handoff_nobody_has_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/handoffs/nothing-here/search-again", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static IProcessorRepository ProcessorRepository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static IJobQueueRepository JobQueueRepository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IJobQueueRepository>();

    private static IDownloadDispatchesRepository DispatchesRepository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IDownloadDispatchesRepository>();

    private static async Task<ProcessorHandoffItem> HandoffWithDispatchAsync(
        ApplicationTestHost app,
        string status,
        string mediaType,
        string entityType,
        string entityId)
    {
        const string clientId = "sabnzbd";
        const string queueItemId = "nzo-1";
        const string releaseName = "Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

        var jobQueueRepository = JobQueueRepository(app);
        var dispatchId = await jobQueueRepository.RecordDownloadDispatchAsync(
            "library-1",
            mediaType,
            entityType,
            entityId,
            releaseName,
            "Indexer",
            clientId,
            "SABnzbd",
            "queued",
            null,
            cancellationToken: CancellationToken.None);
        await DispatchesRepository(app).RecordGrabAsync(
            dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: queueItemId);

        var repository = ProcessorRepository(app);
        var created = await repository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                "library-1",
                mediaType,
                clientId,
                queueItemId,
                releaseName,
                @"C:\Downloads-Complete\TV\Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
                "Weir"),
            CancellationToken.None);
        if (status == ProcessorHandoffStatuses.Waiting)
        {
            return created;
        }

        var updated = await repository.UpdateProcessorHandoffAsync(created.Id, status, null, null, null, CancellationToken.None);
        Assert.NotNull(updated);
        return updated!;
    }
}
