using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #914/#929: one of the choices offered for a hand-off Deluno stopped
/// waiting on is to keep waiting anyway, with a new limit, instead of
/// importing the original or resubmitting to the processor.
/// </summary>
public sealed class ProcessorHandoffKeepWaitingTests
{
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Stalled)]
    [InlineData(ProcessorHandoffStatuses.TimedOut)]
    [InlineData(ProcessorHandoffStatuses.Lost)]
    public async Task A_hand_off_Deluno_stopped_waiting_on_can_be_given_a_new_limit(string ended)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, ended);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/keep-waiting", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Started, stored?.Status);

        var feed = await app.Client.GetAsync("/api/activity?pageSize=50");
        using var body = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.Contains(
            body.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "processing.kept-waiting");
    }

    /// <summary>Nothing to give a new limit to while it is still with the processor, or once it has actually finished.</summary>
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Started)]
    [InlineData(ProcessorHandoffStatuses.Completed)]
    [InlineData(ProcessorHandoffStatuses.Dismissed)]
    public async Task A_hand_off_that_is_not_ended_cannot_be_given_a_new_limit(string status)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, status);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/keep-waiting", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(status, stored?.Status);
    }

    [Fact]
    public async Task A_hand_off_nobody_has_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/handoffs/nothing-here/keep-waiting", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
