using System.Net;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Attention;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>GET /api/attention/summary (#1185): the one definition of "things need a look", on the real host.</summary>
public sealed class AttentionSummaryApiTests
{
    [Fact]
    public async Task An_empty_install_answers_with_no_rows_and_a_count_of_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/attention/summary");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var summary = JsonDocument.Parse(body).RootElement;
        Assert.Equal(0, summary.GetProperty("count").GetInt32());
        Assert.Equal(0, summary.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, summary.GetProperty("notices").GetArrayLength());
        Assert.Equal(JsonValueKind.Number, summary.GetProperty("leftovers").ValueKind);
    }

    [Fact]
    public async Task The_endpoint_and_the_in_process_reading_the_tray_takes_give_the_same_count_and_rows()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var http = JsonDocument.Parse(await app.Client.GetStringAsync("/api/attention/summary")).RootElement;
        var inProcess = await app.Services.GetRequiredService<IAttentionSummaryService>().GetAsync(fresh: true, CancellationToken.None);

        Assert.Equal(inProcess.Count, http.GetProperty("count").GetInt32());
        Assert.Equal(
            inProcess.Rows.Select(row => row.Id).Order(StringComparer.Ordinal),
            http.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()!).Order(StringComparer.Ordinal));
    }
}
