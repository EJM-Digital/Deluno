using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #890 (cleanup sheet F7): once a crashed processor's partial output has
/// been deleted and the hand-off has ended (timed out, stalled, failed, or
/// lost), a person needs a way to hand the original back to the processor
/// again rather than only import it unprocessed or dismiss it. That is this
/// endpoint - the same one "Ask the processor again" on the timeout/failure
/// drawer calls - proven here at the API rather than only in the browser.
/// </summary>
public sealed class ProcessorHandoffRetryTests
{
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    [InlineData(ProcessorHandoffStatuses.TimedOut)]
    [InlineData(ProcessorHandoffStatuses.Stalled)]
    [InlineData(ProcessorHandoffStatuses.Lost)]
    [InlineData(ProcessorHandoffStatuses.Cancelled)]
    public async Task Retrying_a_handoff_that_did_not_finish_hands_it_back_to_the_processor(string endedStatus)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ConnectionAsync(app, "Weir proof");
        var handoff = await HandoffAsync(app, endedStatus, "The processor crashed midway.");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/retry", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, stored?.Status);

        var feed = await app.Client.GetAsync("/api/activity?pageSize=50");
        using var feedBody = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.Contains(
            feedBody.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "processing.retry-requested");
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    [InlineData(ProcessorHandoffStatuses.Started)]
    [InlineData(ProcessorHandoffStatuses.Completed)]
    [InlineData(ProcessorHandoffStatuses.Released)]
    public async Task A_handoff_still_in_flight_or_already_finished_cannot_be_retried(string status)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ConnectionAsync(app, "Weir proof");
        var handoff = await HandoffAsync(app, status, null);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/retry", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(status, stored?.Status);
    }

    [Fact]
    public async Task Retrying_refuses_when_the_processor_connection_is_unavailable()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, ProcessorHandoffStatuses.TimedOut, "No processed file arrived.");

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/retry", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, stored?.Status);
    }

    [Fact]
    public async Task A_handoff_nobody_has_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/handoffs/nothing-here/retry", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<ProcessorConnectionItem> ConnectionAsync(ApplicationTestHost app, string name)
    {
        var connection = await Repository(app).CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest(name, "weir", "http://127.0.0.1:9999", null, null, IsEnabled: true),
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "processor_connections", connection.Id, name);
        return connection with { Name = name };
    }

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
