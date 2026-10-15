using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Attention;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What the Dashboard and the bell are told about a processor hand-off (coverage register, area dashboard), against the real
/// reports of Weir 1.0.0-rc.13 (tests/fixtures/recorded) served to the real host: a hand-off Weir really failed is one thing
/// that needs a person, one still waiting is not, and dismissing the failed one clears it everywhere.
/// </summary>
public sealed class TheDashboardAgainstRecordedWeirTests
{
    private static async Task<JsonElement> SummaryAsync(ApplicationTestHost app)
    {
        // The service keeps its reading for a few seconds; a fresh one is what a person sees after pressing refresh.
        await app.Services.GetRequiredService<IAttentionSummaryService>().GetAsync(fresh: true, CancellationToken.None);
        return JsonDocument.Parse(await app.Client.GetStringAsync("/api/attention/summary")).RootElement.Clone();
    }

    [Fact]
    public async Task A_hand_off_Weir_really_failed_is_a_row_that_needs_a_person_and_a_hand_off_still_with_Weir_is_not()
    {
        await using var weir = await ProcessorRoutesAgainstRecordedWeirTests.WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ProcessorRoutesAgainstRecordedWeirTests.ConnectAsync(app, weir);
        var scene = await ProcessorRoutesAgainstRecordedWeirTests.SceneAsync(app);
        var name = connection.GetProperty("name").GetString();
        var release = ProcessorRoutesAgainstRecordedWeirTests.ReleaseOf("events-failed-under-minimum");
        var failed = await ProcessorRoutesAgainstRecordedWeirTests.HandoffAsync(app, scene, release, name);
        var waiting = await ProcessorRoutesAgainstRecordedWeirTests.HandoffAsync(app, scene, "Still.Waiting.2020.1080p.WEB-DL-DELUNO", name);
        await ProcessorRoutesAgainstRecordedWeirTests.ReportAsync(app, scene, failed, "events-failed-under-minimum");

        var summary = await SummaryAsync(app);

        var rows = summary.GetProperty("rows").EnumerateArray().ToList();
        var row = Assert.Single(rows, item => item.GetProperty("id").GetString() == $"handoff:{failed.Id}");
        Assert.Equal("handoff", row.GetProperty("kind").GetString());
        Assert.Equal(release, row.GetProperty("title").GetString());
        Assert.Equal("Open Transfers", row.GetProperty("action").GetString());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("text").GetString()));
        Assert.DoesNotContain(rows, item => item.GetProperty("id").GetString()!.Contains(waiting.Id, StringComparison.Ordinal));
        Assert.Equal(rows.Sum(item => item.GetProperty("count").GetInt32()), summary.GetProperty("count").GetInt32());
        Assert.True(summary.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task Dismissing_the_failed_hand_off_takes_its_row_out_of_the_summary_and_a_hand_off_still_with_Weir_cannot_be_dismissed()
    {
        await using var weir = await ProcessorRoutesAgainstRecordedWeirTests.WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ProcessorRoutesAgainstRecordedWeirTests.ConnectAsync(app, weir);
        var scene = await ProcessorRoutesAgainstRecordedWeirTests.SceneAsync(app);
        var name = connection.GetProperty("name").GetString();
        var failed = await ProcessorRoutesAgainstRecordedWeirTests.HandoffAsync(app, scene, ProcessorRoutesAgainstRecordedWeirTests.ReleaseOf("events-failed-unreadable"), name);
        var waiting = await ProcessorRoutesAgainstRecordedWeirTests.HandoffAsync(app, scene, "Still.Waiting.2020.1080p.WEB-DL-DELUNO", name);
        await ProcessorRoutesAgainstRecordedWeirTests.ReportAsync(app, scene, failed, "events-failed-unreadable");
        Assert.Contains((await SummaryAsync(app)).GetProperty("rows").EnumerateArray(), item => item.GetProperty("id").GetString() == $"handoff:{failed.Id}");

        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsync($"/api/integrations/processors/handoffs/{waiting.Id}/dismiss", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsync($"/api/integrations/processors/handoffs/{failed.Id}/dismiss", content: null)).StatusCode);

        var after = await SummaryAsync(app);
        Assert.DoesNotContain(after.GetProperty("rows").EnumerateArray(), item => item.GetProperty("id").GetString() == $"handoff:{failed.Id}");
        var listed = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs");
        Assert.Equal("dismissed", listed.EnumerateArray().Single(item => item.GetProperty("id").GetString() == failed.Id).GetProperty("status").GetString());
        Assert.NotEqual("dismissed", listed.EnumerateArray().Single(item => item.GetProperty("id").GetString() == waiting.Id).GetProperty("status").GetString());
    }
}
