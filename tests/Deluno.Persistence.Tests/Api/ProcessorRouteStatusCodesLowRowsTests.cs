using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The status codes of the hand-off and processor-connection routes that no other test asserts: an unknown id is 404, the
/// list defaults to 50 and says whether the processor has heard the outcome, and a path pair belongs to its own connection.
/// </summary>
public sealed class ProcessorRouteStatusCodesLowRowsTests
{
    [Fact]
    public async Task Dismissing_a_hand_off_nobody_has_is_404_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var real = await HandoffAsync(app, "library-1", ProcessorHandoffStatuses.Failed, "ffprobe failed.");

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/handoffs/no-such-hand-off/dismiss", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await Repository(app).GetProcessorHandoffAsync(real.Id, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task The_hand_off_list_holds_fifty_by_default_takes_what_is_asked_for_and_says_which_outcomes_the_processor_has_heard()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await HandoffAsync(app, "library-1", ProcessorHandoffStatuses.Failed, "x");
        for (var index = 1; index < 52; index++)
        {
            await HandoffAsync(app, "library-1", ProcessorHandoffStatuses.Waiting, null, $"Film.{index}.2020.1080p");
        }

        var byDefault = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs");
        var three = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs?take=3");

        Assert.Equal(50, byDefault.GetArrayLength());
        Assert.Equal(3, three.GetArrayLength());
        Assert.All(byDefault.EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("outcome").ValueKind));

        await Repository(app).SaveProcessorOutcomeAsync(
            new ProcessorOutcomeRecord(first.Id, "Weir", "library-1", first.ReleaseName, "imported", null, null, DateTimeOffset.UtcNow, ProcessorOutcomeDeliveries.Delivered, 1, DateTimeOffset.UtcNow, null, false, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var all = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs?take=100");
        var heard = all.EnumerateArray().Single(item => item.GetProperty("id").GetString() == first.Id);
        Assert.Equal(JsonValueKind.Object, heard.GetProperty("outcome").ValueKind);
        Assert.Equal(ProcessorOutcomeDeliveries.Delivered, heard.GetProperty("outcome").GetProperty("delivery").GetString());
    }

    [Fact]
    public async Task A_path_pair_for_a_connection_that_is_not_there_is_404_and_a_pair_is_only_deleted_through_its_own_connection()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var missing = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections/no-such-connection/path-mappings", new { delunoPath = @"C:\Refined", processorPath = "/refined", isEnabled = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var one = await ConnectionAsync(app, "Processor one");
        var two = await ConnectionAsync(app, "Processor two");
        var added = await app.Client.PostAsJsonAsync($"/api/integrations/processors/connections/{one}/path-mappings", new { delunoPath = @"C:\Refined", processorPath = "/refined", isEnabled = true });
        added.EnsureSuccessStatusCode();
        var mappingId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{two}/path-mappings/{mappingId}")).StatusCode);
        Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>($"/api/integrations/processors/connections/{one}/path-mappings")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{one}/path-mappings/{mappingId}")).StatusCode);
    }

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<string> ConnectionAsync(ApplicationTestHost app, string name)
    {
        var created = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new { name, provider = "generic", submissionUrl = "http://127.0.0.1:9/hook", isEnabled = true });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, string libraryId, string status, string? failureMessage, string release = "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO")
    {
        var repository = Repository(app);
        var created = await repository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(libraryId, "movies", "client-1", Guid.NewGuid().ToString("N"), release, @"C:\Downloads\" + release, "Weir proof"),
            CancellationToken.None);
        var updated = await repository.UpdateProcessorHandoffAsync(created.Id, status, null, null, failureMessage, CancellationToken.None);
        Assert.NotNull(updated);
        return updated;
    }
}
