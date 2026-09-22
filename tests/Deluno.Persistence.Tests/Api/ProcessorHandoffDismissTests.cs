using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A hand-off that ended badly can be cleared by a person.
///
/// <para><b>Why this file exists.</b> Before the soak the rig's Transfers screen
/// held hand-offs that nothing could remove. Try again needs the processor
/// connection, which had been removed; Import original refuses a rejected
/// hand-off on purpose; and there was no third choice. They were counted as
/// needing attention for good, which hides the next one that does.</para>
/// </summary>
public sealed class ProcessorHandoffDismissTests
{
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    [InlineData(ProcessorHandoffStatuses.Rejected)]
    [InlineData(ProcessorHandoffStatuses.TimedOut)]
    [InlineData(ProcessorHandoffStatuses.Stalled)]
    [InlineData(ProcessorHandoffStatuses.Lost)]
    [InlineData(ProcessorHandoffStatuses.Cancelled)]
    public async Task A_hand_off_that_ended_without_a_processed_file_can_be_dismissed_and_keeps_its_reason(string ending)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, ending, "ffprobe failed.");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/dismiss", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored.Status);
        Assert.Equal("ffprobe failed.", stored.FailureMessage);
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    [InlineData(ProcessorHandoffStatuses.Started)]
    [InlineData(ProcessorHandoffStatuses.Completed)]
    [InlineData(ProcessorHandoffStatuses.Released)]
    public async Task A_hand_off_that_is_still_in_flight_or_finished_well_cannot_be_dismissed(string status)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, status, null);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/dismiss", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(status, stored?.Status);
    }

    /// <summary>
    /// Weir delivered after the film was removed, so the hand-off ended
    /// <c>completed</c> carrying a reason. Transfers counts it, and what is
    /// counted must be clearable (#570).
    /// </summary>
    [Fact]
    public async Task A_hand_off_that_finished_with_something_to_say_can_be_dismissed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(
            app,
            ProcessorHandoffStatuses.Completed,
            "Weir produced a cleaned file, but the film was removed from Deluno, so Deluno did not import it.");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/dismiss", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored?.Status);
    }

    [Fact]
    public async Task Dismissing_is_recorded_in_the_feed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, ProcessorHandoffStatuses.Rejected, "Weir could not process it.");

        await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/dismiss", new { });

        var response = await app.Client.GetAsync("/api/activity?pageSize=50");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(
            body.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "processing.dismissed");
    }

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, string status, string? failureMessage)
    {
        var repository = Repository(app);
        var created = await repository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                "library-1",
                "movies",
                "client-1",
                "hash-1",
                "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
                @"C:\Downloads\The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
                "Weir proof"),
            CancellationToken.None);
        var updated = await repository.UpdateProcessorHandoffAsync(created.Id, status, null, null, failureMessage, CancellationToken.None);
        Assert.NotNull(updated);
        return updated;
    }
}
