using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The update routes tell every open System screen that the status changed (UpdateStatusChanged), so a screen left open shows
/// a check, a download, a restart or a saved preference with no reload (#1329). The routes run on the real host; the push is
/// the one the host's own publisher is asked for.
/// </summary>
public sealed class TheUpdateRoutesPushTheirStatusTests
{
    private static int StatusPushes(RecordingRealtimeEventPublisher pushes)
        => pushes.Published.Count(item => item.Type == "UpdateStatus" && item.Id == "updates");

    [Fact]
    public async Task Check_download_restart_and_a_saved_preference_each_push_one_status_change_and_a_read_pushes_none()
    {
        var pushes = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(pushes)));

        using var read = await app.Client.GetAsync("/api/updates/status");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        // The first read may note the version this run started on; after that a read pushes nothing.
        var baseline = StatusPushes(pushes);
        using var readAgain = await app.Client.GetAsync("/api/updates/status");
        Assert.Equal(HttpStatusCode.OK, readAgain.StatusCode);
        Assert.Equal(baseline, StatusPushes(pushes));

        using var check = await app.Client.PostAsync("/api/updates/check", null);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal(baseline + 1, StatusPushes(pushes));

        using var download = await app.Client.PostAsync("/api/updates/download", null);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(baseline + 2, StatusPushes(pushes));

        using var restart = await app.Client.PostAsync("/api/updates/restart-now", null);
        Assert.Equal(HttpStatusCode.OK, restart.StatusCode);
        var restartBody = await restart.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(restartBody.GetProperty("accepted").GetBoolean());
        Assert.Equal(baseline + 3, StatusPushes(pushes));

        using var saved = await app.Client.PutAsJsonAsync("/api/updates/preferences", new { mode = "download-background", channel = "stable", autoCheck = false });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(baseline + 4, StatusPushes(pushes));

        var preferences = await app.Client.GetFromJsonAsync<JsonElement>("/api/updates/preferences");
        Assert.Equal("download-background", preferences.GetProperty("mode").GetString());
        Assert.False(preferences.GetProperty("autoCheck").GetBoolean());
        Assert.Equal(baseline + 4, StatusPushes(pushes));
    }
}
