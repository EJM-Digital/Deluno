using Deluno.Persistence.Tests.Support;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1218: a failed row in a download client's history is read from the client, so dismissing it is remembered by Deluno.</summary>
public sealed class AFailedClientHistoryRowCanBeDismissedTests
{
    [Fact]
    public async Task A_dismissed_history_row_is_remembered_and_listed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Empty((await app.Client.GetFromJsonAsync<string[]>("/api/download-clients/history/dismissed"))!);

        var response = await app.Client.PostAsJsonAsync("/api/download-clients/nzbget/history/item-1/dismiss", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(["client-history:nzbget:item-1"], (await app.Client.GetFromJsonAsync<string[]>("/api/download-clients/history/dismissed"))!);

        // Dismissing it again changes nothing.
        Assert.True((await app.Client.PostAsJsonAsync("/api/download-clients/nzbget/history/item-1/dismiss", new { })).IsSuccessStatusCode);
        Assert.Single((await app.Client.GetFromJsonAsync<string[]>("/api/download-clients/history/dismissed"))!);
    }
}
