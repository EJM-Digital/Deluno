using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What a person is told when the service a notification goes to says no (coverage register, integrations 6): a wrong login or address is named
/// as a login problem or a refused request, and a rate limit as a rate limit, each kept on the delivery and on the connection. The 401 and 403
/// replies are the ones ntfy 2.32.0 and Gotify 3.1.1 really gave (tests/fixtures/recorded/ntfy and gotify, served over a loopback port to the
/// whole application with its own databases). A 404 and a 429 are status lines only (no app's body is involved: the answer is the status that
/// Deluno classifies), served by a plain loopback address.
/// </summary>
public sealed class NotificationServicesThatRefuseOrSlowDownTests
{
    private static async Task TurnOnAsync(ApplicationTestHost app)
        => Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);

    private static async Task<string> AddAsync(ApplicationTestHost app, string name, string url, string provider, string? configJson = null)
    {
        var made = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name, url, eventFilters = "all", isEnabled = true, provider, configJson });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static Task SayAsync(ApplicationTestHost app, string title = "Import completed: Sintel (2010)")
        => app.Services.GetRequiredService<IOutboundNotificationService>().DispatchAsync("import.completed", title, "Sintel (2010) was imported.", null, CancellationToken.None);

    private static async Task<JsonElement> OnlyDeliveryAsync(ApplicationTestHost app)
        => Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries")).EnumerateArray());

    private static async Task<JsonElement> WebhookAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    private static void AssertSaid(JsonElement delivery, int status, string kind, string nextActionContains)
    {
        Assert.Equal("dead-letter", delivery.GetProperty("status").GetString());
        Assert.Equal(status, delivery.GetProperty("lastStatusCode").GetInt32());
        Assert.Contains($"HTTP {status}", delivery.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        var failure = delivery.GetProperty("failure");
        Assert.Equal(kind, failure.GetProperty("kind").GetString(), ignoreCase: true);
        Assert.Equal(status, failure.GetProperty("httpStatus").GetInt32());
        Assert.Equal("manualAction", failure.GetProperty("retryState").GetString(), ignoreCase: true);
        Assert.Contains(nextActionContains, failure.GetProperty("nextAction").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, 1, 403)]
    [InlineData(true, 2, 401)]
    public async Task An_ntfy_that_wants_a_login_is_named_as_a_login_problem_whether_none_was_given_or_the_wrong_one(bool sendAWrongToken, int recordedExchange, int refusal)
    {
        // The recorded server: the right token is taken, none is refused 403, a wrong one 401.
        await using var ntfy = await RecordedAppServer.StartAsync("ntfy", [], (_, _) => RecordedReplies.Ntfy("access-token").Exchanges[recordedExchange].Reply.ToResponse());
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, "Phone", ntfy.BaseUrl + "/deluno-films", "ntfy",
            sendAWrongToken ? JsonSerializer.Serialize(new { accessToken = "tk_wrongwrongwrongwrongwrongwrongx" }) : null);
        Assert.Equal(refusal, RecordedReplies.Ntfy("access-token").Exchanges[recordedExchange].Reply.Status);

        await SayAsync(app);

        AssertSaid(await OnlyDeliveryAsync(app), refusal, "authentication", "credential");
        Assert.Contains($"HTTP {refusal}", (await WebhookAsync(app, id)).GetProperty("lastError").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Gotify_that_refuses_its_token_is_named_as_a_login_problem()
    {
        await using var gotify = await RecordedAppServer.StartAsync("gotify", [], (_, _) => RecordedReplies.Gotify("refused").Exchanges[0].Reply.ToResponse());
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, "Push", gotify.BaseUrl, "gotify", JsonSerializer.Serialize(new { appToken = "NotAKeyGotifyKnows" }));
        Assert.Equal(401, RecordedReplies.Gotify("refused").Exchanges[0].Reply.Status);

        await SayAsync(app);

        AssertSaid(await OnlyDeliveryAsync(app), 401, "authentication", "credential");
    }

    [Fact]
    public async Task An_address_that_answers_404_is_a_refused_request_to_check_not_a_login_problem()
    {
        await using var nowhere = await RecordedAppServer.StartAsync("ntfy", [], (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, "Wrong address", nowhere.BaseUrl + "/hooks/gone", "generic");

        await SayAsync(app);

        Assert.Equal(3, nowhere.Requests.Count);
        AssertSaid(await OnlyDeliveryAsync(app), 404, "rejectedAction", "Review the provider response");
    }

    [Fact]
    public async Task A_service_that_answers_429_with_a_Retry_After_is_named_as_rate_limited_and_the_send_is_retried_on_Delunos_own_schedule()
    {
        await using var busy = await RecordedAppServer.StartAsync("ntfy", [], (_, _) =>
        {
            var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            reply.Headers.TryAddWithoutValidation("Retry-After", "120");
            return reply;
        });
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, "Chat", busy.BaseUrl + "/hook", "generic");

        await SayAsync(app);

        // The three tries are Deluno's own 0 s, 2 s and 5 s schedule; the service's two minutes is not waited for (it would hold every other event behind it).
        Assert.Equal(3, busy.Requests.Count);
        var delivery = await OnlyDeliveryAsync(app);
        AssertSaid(delivery, 429, "rateLimit", "rate limit");
        Assert.Equal(3, delivery.GetProperty("attemptCount").GetInt32());
        Assert.Contains("HTTP 429", (await WebhookAsync(app, id)).GetProperty("lastError").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_429_that_clears_on_the_next_try_is_delivered_and_leaves_no_error_behind()
    {
        var calls = 0;
        await using var busy = await RecordedAppServer.StartAsync("ntfy", [], (_, _) =>
            Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK));
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        var id = await AddAsync(app, "Chat", busy.BaseUrl + "/hook", "generic");

        await SayAsync(app);

        var delivery = await OnlyDeliveryAsync(app);
        Assert.Equal(("delivered", 2, 200), (delivery.GetProperty("status").GetString(), delivery.GetProperty("attemptCount").GetInt32(), delivery.GetProperty("lastStatusCode").GetInt32()));
        Assert.True(string.IsNullOrEmpty((await WebhookAsync(app, id)).GetProperty("lastError").GetString()));
    }

    [Fact]
    public async Task A_burst_of_events_is_sent_one_after_another_and_none_is_dropped_or_merged()
    {
        await using var receiver = await RecordedAppServer.StartAsync("ntfy", [], (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, "Chat", receiver.BaseUrl + "/hook", "generic");

        for (var i = 1; i <= 12; i++)
        {
            await SayAsync(app, $"Import completed: Film {i}");
        }

        Assert.Equal(12, receiver.Requests.Count);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"Import completed: Film {i}"),
            receiver.Requests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("title").GetString()));
        var deliveries = (await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries")).EnumerateArray().ToList();
        Assert.Equal(12, deliveries.Count);
        Assert.All(deliveries, delivery => Assert.Equal("delivered", delivery.GetProperty("status").GetString()));
    }
}
