using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Low-severity rows (#1329) of the bulk queue removal and the stats address, on the whole application with its own databases.</summary>
public sealed class QueueBulkRemovalAndStatsLowGapTests
{
    private static async Task<(HttpStatusCode Status, string Body)> BulkAsync(ApplicationTestHost app, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/queue/bulk")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        var response = await app.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_bulk_removal_with_no_downloads_or_one_missing_its_client_or_queue_id_is_a_400_and_so_are_more_than_two_hundred()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var empty = await BulkAsync(app, new { items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Contains("Name at least one download, each with its clientId and queueItemId.", empty.Body);

        var none = await BulkAsync(app, new { });
        Assert.Equal(HttpStatusCode.BadRequest, none.Status);

        var noQueueId = await BulkAsync(app, new { items = new object[] { new { clientId = "client-1", queueItemId = "abc" }, new { clientId = "client-1" } } });
        Assert.Equal(HttpStatusCode.BadRequest, noQueueId.Status);
        Assert.Contains("Name at least one download", noQueueId.Body);

        var noClient = await BulkAsync(app, new { items = new object[] { new { clientId = " ", queueItemId = "abc" } } });
        Assert.Equal(HttpStatusCode.BadRequest, noClient.Status);

        var tooMany = await BulkAsync(app, new { items = Enumerable.Range(0, 201).Select(i => new { clientId = "client-1", queueItemId = $"item-{i}" }).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.Status);
        Assert.Contains("Remove at most 200 downloads in one call.", tooMany.Body);

        // Exactly two hundred passes the count check (the downloads are not Deluno's, so it is turned away later, not as a 400).
        var exactly = await BulkAsync(app, new { items = Enumerable.Range(0, 200).Select(i => new { clientId = "client-1", queueItemId = $"item-{i}" }).ToArray() });
        Assert.NotEqual(HttpStatusCode.BadRequest, exactly.Status);
    }

    [Fact]
    public async Task The_stats_address_is_read_by_a_read_only_key_and_refuses_a_caller_who_is_not_signed_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Dashboard", scopes = "read" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;

        using var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/stats")).StatusCode);

        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/stats")).StatusCode);
    }
}
