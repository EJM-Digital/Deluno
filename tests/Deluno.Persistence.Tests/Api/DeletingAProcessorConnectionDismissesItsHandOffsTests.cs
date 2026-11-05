using System.Net;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;
using static Deluno.Persistence.Tests.Api.ProcessorRoutesAgainstRecordedWeirTests;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Deleting a processor connection dismisses the hand-offs still with it and
/// leaves a processor that is already mid-run alone (coverage register, engines).
/// The processor's answers are Weir's recorded real ones (handoff-cancelled,
/// handoff-cancel-working), served over a real loopback port.
/// </summary>
public sealed class DeletingAProcessorConnectionDismissesItsHandOffsTests
{
    [Fact]
    public async Task A_queued_hand_off_is_dismissed_with_its_connection_and_one_already_mid_run_is_left_alone()
    {
        var cancelled = RecordedReplies.Weir("handoff-cancelled").Exchanges[0];
        var working = RecordedReplies.Weir("handoff-cancel-working").Exchanges[0];
        string? busyId = null;
        await using var weir = await WeirAsync(
            (request, _) => request.Method != HttpMethod.Delete
                ? null
                : request.RequestUri!.AbsolutePath.EndsWith("/" + busyId, StringComparison.Ordinal) ? working.Reply.ToResponse() : cancelled.Reply.ToResponse(),
            "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ConnectAsync(app, weir);
        var id = connection.GetProperty("id").GetString()!;
        var name = connection.GetProperty("name").GetString();
        var scene = await SceneAsync(app);
        var queued = await HandoffAsync(app, scene, "Queued.Film.2020.1080p.WEB-DL-DELUNO", name);
        var busy = await HandoffAsync(app, scene, "Busy.Film.2020.1080p.WEB-DL-DELUNO", name);
        busyId = busy.Id;
        Assert.Equal(id, queued.ProcessorConnectionId);

        var response = await app.Client.DeleteAsync($"/api/integrations/processors/connections/{id}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var ended = await Repository(app).GetProcessorHandoffAsync(queued.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, ended!.Status);
        Assert.Contains("was removed from Deluno", ended.FailureMessage);
        var left = await Repository(app).GetProcessorHandoffAsync(busy.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, left!.Status);
    }

    [Fact]
    public async Task Deleting_a_connection_that_is_not_there_answers_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.DeleteAsync("/api/integrations/processors/connections/no-such-connection");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
