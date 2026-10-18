using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// <c>GET /api/download-clients/telemetry</c> is what Transfers reads. Against a real qBittorrent 5.2.1 reply (tests/fixtures/recorded,
/// changed only to hold a second torrent that another tool added): the clients with their capabilities and summary, a queue
/// narrowed to what Deluno itself sent, the age of the reading, and no call to the client when it is read again.
/// </summary>
public sealed class TheTelemetryRouteServesTheLastCaptureOfDelunosOwnDownloadsTests
{
    private const string ForeignHash = "f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0";

    [Fact]
    public async Task It_lists_the_client_with_its_capabilities_and_only_the_download_Deluno_sent_and_a_second_read_does_not_call_the_client()
    {
        var info = JsonNode.Parse(RecordedReplies.Qbittorrent("downloading").Body("torrents/info"))!.AsArray();
        var ours = info[0]!.AsObject();
        var oursHash = ours["hash"]!.GetValue<string>();
        var oursName = ours["name"]!.GetValue<string>();
        var foreign = JsonNode.Parse(ours.ToJsonString())!.AsObject();
        foreign["hash"] = ForeignHash;
        foreign["name"] = "Added.By.Another.Tool.2020.1080p";
        foreign["content_path"] = @"C:\Downloads\Added.By.Another.Tool.2020.1080p";
        info.Add(foreign);
        var listing = info.ToJsonString();

        await using var app = await ApplicationTestHost.StartAsync();
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "trackers" }.Select(RecordedReplies.Qbittorrent),
            (request, _) => request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) ? RecordedReplies.Json(listing) : null);
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var clientId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync("library-1", "movies", "movie", "movie-1", oursName, "Fixture", clientId, "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordDetectionAsync(dispatchId, oursHash, null, completed: false, CancellationToken.None);

        using var response = await app.Client.GetAsync("/api/download-clients/telemetry");

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(response.Headers.TryGetValues("X-Deluno-Snapshot-Age-Ms", out var age) && long.Parse(age.Single()) >= 0, "the reading says how old it is");
        var overview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(overview.TryGetProperty("capturedUtc", out var captured) && captured.GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        var snapshot = Assert.Single(overview.GetProperty("clients").EnumerateArray());
        Assert.Equal("qbittorrent", snapshot.GetProperty("protocol").GetString());
        var capabilities = snapshot.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("supportsQueueReorder").GetBoolean());
        Assert.True(capabilities.GetProperty("supportsForceStart").GetBoolean());
        Assert.True(capabilities.GetProperty("supportsRecheck").GetBoolean());
        Assert.True(overview.GetProperty("summary").TryGetProperty("totalSpeedMbps", out _));
        // Only what Deluno itself sent: the second torrent, which another tool put in the same client, is not listed.
        var queue = snapshot.GetProperty("queue").EnumerateArray().ToArray();
        var listed = Assert.Single(queue);
        Assert.Equal(oursHash, listed.GetProperty("id").GetString(), ignoreCase: true);
        Assert.DoesNotContain(queue, item => item.GetProperty("releaseName").GetString()!.Contains("Another.Tool", StringComparison.Ordinal));

        // Read again with the client switched off: the same reading comes back from the last capture, with no call to the client.
        var callsBefore = qbit.Requests.Count;
        using var again = await app.Client.GetAsync("/api/download-clients/telemetry");
        var second = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetProperty("clients")[0].GetProperty("queue").GetArrayLength());
        Assert.Equal(callsBefore, qbit.Requests.Count);
    }
}
