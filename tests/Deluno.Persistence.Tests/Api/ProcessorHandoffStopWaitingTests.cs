using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A person can stop waiting for a processor that is never going to finish (#566).
///
/// <para><b>Why this file exists.</b> Deluno waits while a processor says it is
/// queued, however long that takes, and gives up only after the library's limit
/// — measured from the processor's last change or from it answering again, so
/// restarting the processor starts the wait over. On the rig four hand-offs sat
/// at "queued" for two days for work Weir no longer had, and no screen could end
/// one: retry, import-original and dismiss are all offered once it has ended.</para>
/// </summary>
public sealed class ProcessorHandoffStopWaitingTests
{
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    [InlineData(ProcessorHandoffStatuses.Accepted)]
    [InlineData(ProcessorHandoffStatuses.Started)]
    public async Task A_hand_off_still_with_the_processor_can_be_ended_by_a_person(string status)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, status);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/stop-waiting", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, stored?.Status);
        // What happened is on the hand-off, so Transfers can say it rather than
        // showing an ending with no reason.
        Assert.Contains("stopped waiting", stored?.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An ending is already an ending: dismiss is the action for those.</summary>
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Completed)]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    [InlineData(ProcessorHandoffStatuses.Cancelled)]
    [InlineData(ProcessorHandoffStatuses.Dismissed)]
    public async Task A_hand_off_that_has_ended_has_nothing_left_to_wait_for(string ending)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, ending);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/stop-waiting", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ending, stored?.Status);
    }

    [Fact]
    public async Task A_hand_off_nobody_has_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/handoffs/nothing-here/stop-waiting", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The processor could not be told — there is no connection by that name here
    /// — and the answer says so rather than pretending it was dropped.
    /// </summary>
    [Fact]
    public async Task Stopping_says_the_processor_was_not_told_and_records_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, ProcessorHandoffStatuses.Submitted);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/stop-waiting", new { });

        using var said = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("could not be told", said.RootElement.GetProperty("message").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var feed = await app.Client.GetAsync("/api/activity?pageSize=50");
        using var body = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.Contains(
            body.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "processing.cancelled");
    }

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, string status)
    {
        var repository = Repository(app);
        var created = await repository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                "library-1",
                "tv",
                "sabnzbd",
                "nzo-1",
                "Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
                @"C:\Downloads-Complete\TV\Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
                "Weir"),
            CancellationToken.None);
        if (status == ProcessorHandoffStatuses.Waiting) return created;
        var updated = await repository.UpdateProcessorHandoffAsync(created.Id, status, null, null, null, CancellationToken.None);
        Assert.NotNull(updated);
        return updated;
    }
}
