using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Updates;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Check now" on the Updates screen (coverage register, area api): on the whole application it answers what this install is and whether
/// it can update itself; and when the update feed cannot be read it says so in the feed's own sentence, never "Checked for updates.".
/// </summary>
public sealed class CheckingForANewReleaseSaysWhatHappenedTests
{
    private sealed class FeedDown : IUpdateOrchestrator
    {
        private static UpdateStatusResponse Down() => new("1.0.0", "stable", "windows", "manual", true, true, true, true, false, null, "error", null, false, null, null,
            "Couldn't check for updates: the update feed isn't reachable.", "feed unreachable", []);

        public Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Down());
        public Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken) => Task.FromResult(Down());
        public Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken) => Task.FromResult(Down());
        public Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken) => Task.FromResult(Down());
        public Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken) => Task.FromResult(new UpdatePreferencesResponse(UpdateModes.NotifyOnly, "stable", false));
        public Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken) => GetPreferencesAsync(cancellationToken);
    }

    [Fact]
    public async Task Checking_on_the_whole_application_answers_that_it_checked_and_what_this_install_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/updates/check", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal("Checked for updates.", body.GetProperty("message").GetString());
        var status = body.GetProperty("status");
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("currentVersion").GetString()));
        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
        Assert.NotEqual("error", status.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsync("/api/updates/check", content: null)).StatusCode);
    }

    [Fact]
    public async Task Checking_when_the_update_feed_cannot_be_read_says_so_in_the_feeds_own_sentence_and_carries_the_error_state()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IUpdateOrchestrator>();
            services.AddSingleton<IUpdateOrchestrator>(new FeedDown());
        });

        var response = await app.Client.PostAsync("/api/updates/check", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Couldn't check for updates: the update feed isn't reachable.", body.GetProperty("message").GetString());
        Assert.Equal("error", body.GetProperty("status").GetProperty("state").GetString());
    }
}
