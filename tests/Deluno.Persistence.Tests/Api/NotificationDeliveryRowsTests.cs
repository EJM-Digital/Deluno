using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// How a delivery to a notification connection is kept and retried, on the whole application with its own database and a receiver
/// that answers as the real ntfy 2.34.0 did (tests/fixtures/recorded/ntfy): the delivery is written as pending before the send,
/// with what a replay needs; a refusal is retried at once, after 2 s and after 5 s whatever the status; and a cancelled send is
/// not swallowed or turned into a failed delivery.
/// </summary>
public sealed class NotificationDeliveryRowsTests
{
    private const string Title = "Import completed: Sintel (2010)";
    private const string Message = "Sintel (2010) was imported from Sintel.2010.WEB.1080p using copy.";

    private static async Task TurnOnAsync(ApplicationTestHost app)
        => Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);

    private static async Task<string> AddAsync(ApplicationTestHost app, string url, string provider)
    {
        var made = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Phone", url, eventFilters = "all", isEnabled = true, provider, configJson = (string?)null });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static Task SayAsync(ApplicationTestHost app, string? detailsJson = null, CancellationToken cancellationToken = default)
        => app.Services.GetRequiredService<IOutboundNotificationService>().DispatchAsync("import.completed", Title, Message, detailsJson, cancellationToken);

    private static async Task<JsonElement> DeliveriesAsync(ApplicationTestHost app)
        => await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks/deliveries");

    [Fact]
    public async Task A_delivery_is_written_as_pending_with_its_category_and_title_before_the_send_and_a_replay_sends_the_details_it_kept()
    {
        JsonElement duringTheSend = default;
        Func<Task<JsonElement>>? readDeliveries = null;
        await using var receiver = await RecordedAppServer.StartAsync("ntfy", [], (_, _) =>
        {
            duringTheSend = readDeliveries!().GetAwaiter().GetResult();
            return RecordedReplies.Ntfy("delivered").Exchanges[0].Reply.ToResponse();
        });
        await using var app = await ApplicationTestHost.StartAsync();
        readDeliveries = () => DeliveriesAsync(app);
        await TurnOnAsync(app);
        await AddAsync(app, receiver.BaseUrl + "/hook", "generic");

        await SayAsync(app, "{\"film\":\"Sintel\",\"size\":42}");

        var pending = Assert.Single(duringTheSend.EnumerateArray());
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        Assert.Equal("import.completed", pending.GetProperty("eventCategory").GetString());
        Assert.Equal(Title, pending.GetProperty("title").GetString());
        var delivered = Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        Assert.Equal("delivered", delivered.GetProperty("status").GetString());

        var replay = await app.Client.PostAsync($"/api/notification-webhooks/deliveries/{delivered.GetProperty("id").GetString()}/replay", null);
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        Assert.Equal(2, receiver.Requests.Count);
        var first = JsonNode.Parse(receiver.Requests[0].Body)!;
        var second = JsonNode.Parse(receiver.Requests[1].Body)!;
        Assert.Equal(Title, second["title"]!.GetValue<string>());
        Assert.Equal(Message, second["message"]!.GetValue<string>());
        Assert.Equal(42, second["details"]!["size"]!.GetValue<int>());
        Assert.Equal(first["details"]!.ToJsonString(), second["details"]!.ToJsonString());
    }

    [Fact]
    public async Task A_refusal_is_retried_at_once_then_after_two_seconds_and_then_after_five_whatever_the_status_and_then_dead_lettered()
    {
        var clock = Stopwatch.StartNew();
        var arrivals = new List<TimeSpan>();
        await using var receiver = await RecordedAppServer.StartAsync("ntfy", [], (_, _) =>
        {
            lock (arrivals) { arrivals.Add(clock.Elapsed); }
            return RecordedReplies.Ntfy("access-token").Exchanges[1].Reply.ToResponse();
        });
        Assert.Equal(403, RecordedReplies.Ntfy("access-token").Exchanges[1].Reply.Status);
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, receiver.BaseUrl + "/deluno-films", "ntfy");

        await SayAsync(app);

        Assert.Equal(3, arrivals.Count);
        Assert.InRange((arrivals[1] - arrivals[0]).TotalSeconds, 1.8, 4);
        Assert.InRange((arrivals[2] - arrivals[1]).TotalSeconds, 4.8, 8);
        var delivery = Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        Assert.Equal(("dead-letter", 3), (delivery.GetProperty("status").GetString(), delivery.GetProperty("attemptCount").GetInt32()));
    }

    [Fact]
    public async Task A_send_that_is_cancelled_between_attempts_throws_to_the_caller_and_is_not_dead_lettered()
    {
        using var cancellation = new CancellationTokenSource();
        await using var receiver = await RecordedAppServer.StartAsync("ntfy", [], (_, _) =>
        {
            cancellation.Cancel();
            return RecordedReplies.Ntfy("access-token").Exchanges[1].Reply.ToResponse();
        });
        await using var app = await ApplicationTestHost.StartAsync();
        await TurnOnAsync(app);
        await AddAsync(app, receiver.BaseUrl + "/deluno-films", "ntfy");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SayAsync(app, cancellationToken: cancellation.Token));

        Assert.Single(receiver.Requests);
        var delivery = Assert.Single((await DeliveriesAsync(app)).EnumerateArray());
        Assert.NotEqual("dead-letter", delivery.GetProperty("status").GetString());
        Assert.NotEqual("delivered", delivery.GetProperty("status").GetString());
    }
}
