using System.Net;
using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The hooks only the browser tests use to push a realtime event (coverage register, area api). Switched on, each hands the server's own
/// publisher exactly what it was asked to push, needs a signed-in caller, and a request that names nothing is refused.
/// </summary>
public sealed class TheRealtimePushHooksPushWhatTheyAreAskedTests
{
    private static async Task<(ApplicationTestHost App, RecordingRealtimeEventPublisher Publisher)> StartAsync()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        var app = await ApplicationTestHost.StartAsync(
            replaceServices: services =>
            {
                services.RemoveAll<IRealtimeEventPublisher>();
                services.AddSingleton<IRealtimeEventPublisher>(publisher);
            },
            settings: new Dictionary<string, string?> { ["Testing:EnableRealtimePushEndpoint"] = "true" });
        return (app, publisher);
    }

    [Fact]
    public async Task Pushing_download_progress_publishes_that_download_and_needs_a_signed_in_caller()
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;

        var response = await app.Client.PostAsJsonAsync("/api/testing/realtime/download-progress", new { id = "dl-1", title = "Dune", progress = 42.5, speedMbps = 8.0, eta = "00:10:00", status = "downloading" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["dl-1"], publisher.DownloadProgressIds);
        var anonymous = await app.CreateAnonymousClient().PostAsJsonAsync("/api/testing/realtime/download-progress", new { id = "dl-2", progress = 1.0, speedMbps = 1.0 });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(["dl-1"], publisher.DownloadProgressIds);
    }

    [Fact]
    public async Task Pushing_an_activity_line_publishes_it_with_the_category_and_severity_asked_or_the_plain_defaults()
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;

        var asked = await app.Client.PostAsJsonAsync("/api/testing/realtime/activity", new { message = "A file was imported.", category = "import", severity = "warning" });
        var plain = await app.Client.PostAsJsonAsync("/api/testing/realtime/activity", new { message = "Searched." });

        Assert.Equal(HttpStatusCode.NoContent, asked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, plain.StatusCode);
        Assert.Equal([("import", "warning"), ("search", "info")], publisher.ActivityEvents);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/testing/realtime/activity", new { message = "x" })).StatusCode);
    }

    [Fact]
    public async Task Pushing_a_live_event_publishes_its_name_and_its_own_data_on_the_live_subject()
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;

        var response = await app.Client.PostAsJsonAsync("/api/testing/realtime/live-event", new { eventName = "JobChanged", payload = new { id = "job-1", state = "failed" } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var pushed = Assert.Single(publisher.LiveEvents);
        Assert.Equal("JobChanged", pushed.Name);
        Assert.Contains("job-1", System.Text.Json.JsonSerializer.Serialize(pushed.Payload));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/testing/realtime/live-event", new { eventName = "JobChanged", payload = new { } })).StatusCode);
    }

    [Fact]
    public async Task Pushing_a_named_event_goes_to_the_dashboard_subject_or_the_live_one_as_asked_and_a_request_with_no_name_is_refused()
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;

        var dashboard = await app.Client.PostAsJsonAsync("/api/testing/realtime/event", new { eventName = "AttentionChanged", payload = new { count = 3 } });
        var live = await app.Client.PostAsJsonAsync("/api/testing/realtime/event", new { eventName = "ClientStateChanged", subject = "live", payload = new { id = "c1" } });
        var nameless = await app.Client.PostAsJsonAsync("/api/testing/realtime/event", new { eventName = " ", payload = new { } });

        Assert.Equal(HttpStatusCode.NoContent, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, live.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);
        Assert.Equal("AttentionChanged", Assert.Single(publisher.DashboardEvents).Name);
        Assert.Equal("ClientStateChanged", Assert.Single(publisher.LiveEvents).Name);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/testing/realtime/event", new { eventName = "x" })).StatusCode);
    }

    [Fact]
    public async Task Announcing_that_a_film_a_show_or_an_episode_changed_publishes_the_same_change_a_database_write_does()
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;

        foreach (var (kind, id) in new[] { ("Movie", "m1"), ("Series", "s1"), ("Episode", "e1") })
        {
            var response = await app.Client.PostAsJsonAsync("/api/testing/realtime/title-changed", new { entityType = kind, id, libraryIds = new[] { "lib" } });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal([("Movie", "m1"), ("Series", "s1"), ("Episode", "e1")], publisher.Published);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/testing/realtime/title-changed", new { entityType = "Movie", id = "m2" })).StatusCode);
    }
}
