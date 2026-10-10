using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What Deluno owes a processor is written down, and a person can see it and
/// have it said again (#580).
///
/// <para><b>Why this file exists.</b> Deluno told a processor what became of a
/// file once. When that did not land it wrote a line in Activity and moved on,
/// so the processor kept its copy for good and no screen counted it. The
/// report now lives in the database until it is heard.</para>
/// </summary>
public sealed class ProcessorOutcomeOwedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T09:00:00Z");

    [Fact]
    public async Task Only_what_is_still_owed_and_due_is_sent_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = Repository(app);
        await repository.SaveProcessorOutcomeAsync(Owed("due") with { NextAttemptUtc = Now.AddMinutes(-1) }, CancellationToken.None);
        await repository.SaveProcessorOutcomeAsync(Owed("later") with { NextAttemptUtc = Now.AddMinutes(10) }, CancellationToken.None);
        await repository.SaveProcessorOutcomeAsync(Owed("heard") with { Delivery = ProcessorOutcomeDeliveries.Delivered, NextAttemptUtc = null }, CancellationToken.None);

        var due = await repository.ListDueProcessorOutcomesAsync(Now, CancellationToken.None);

        Assert.Equal("due", Assert.Single(due).HandoffId);
    }

    [Fact]
    public async Task A_record_reads_back_as_it_was_written()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = Repository(app);
        var written = Owed("h-1") with { Attempts = 3, LastAttemptUtc = Now, Released = true, ProcessorSaid = "Nothing for Weir to remove." };

        await repository.SaveProcessorOutcomeAsync(written, CancellationToken.None);
        await repository.SaveProcessorOutcomeAsync(written with { Delivery = ProcessorOutcomeDeliveries.Delivered }, CancellationToken.None);

        Assert.Equal(written with { Delivery = ProcessorOutcomeDeliveries.Delivered }, await repository.GetProcessorOutcomeAsync("h-1", CancellationToken.None));
    }

    [Fact]
    public async Task Transfers_sees_whether_the_processor_has_heard()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await HandoffAsync(app, "hash-1");
        await Repository(app).SaveProcessorOutcomeAsync(Owed(handoff.Id), CancellationToken.None);

        var listed = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs?take=10");

        var item = Assert.Single(listed.EnumerateArray());
        Assert.Equal(ProcessorOutcomeDeliveries.Pending, item.GetProperty("outcome").GetProperty("delivery").GetString());
    }

    /// <summary>
    /// A new download of the same release restarts the hand-off row under a
    /// new id. The processor keys the earlier outcome on the earlier id, so it
    /// must still be owed under that one.
    /// </summary>
    [Fact]
    public async Task A_new_download_of_the_same_release_does_not_lose_what_the_last_one_owed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = Repository(app);
        var first = await HandoffAsync(app, "hash-1");
        await repository.UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Completed, null, null, null, CancellationToken.None);
        await repository.SaveProcessorOutcomeAsync(Owed(first.Id), CancellationToken.None);

        var second = await HandoffAsync(app, "hash-2");

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotNull(await repository.GetProcessorOutcomeAsync(first.Id, CancellationToken.None));
    }

    // #1221: dismissing a hand-off the processor failed keeps the fact that it failed, since the status itself goes.
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Failed, true)]
    [InlineData(ProcessorHandoffStatuses.Rejected, true)]
    [InlineData(ProcessorHandoffStatuses.TimedOut, false)]
    public async Task Dismissing_a_hand_off_the_processor_failed_keeps_that_it_failed(string status, bool expected)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = Repository(app);
        var handoff = await HandoffAsync(app, "hash-1");
        await repository.UpdateProcessorHandoffAsync(handoff.Id, status, null, null, "It went wrong.", CancellationToken.None);
        await repository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, "It went wrong.", CancellationToken.None);

        var stored = await repository.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);

        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored?.Status);
        Assert.Equal(expected, ProcessorHandoffStatuses.ProcessorReportedItFailed(stored!));
    }

    [Fact]
    public async Task Saying_it_again_needs_something_to_say()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/handoffs/nothing-here/outcome/send-again", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Saying_it_again_to_a_removed_processor_says_there_is_nobody_to_tell()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await Repository(app).SaveProcessorOutcomeAsync(Owed("h-1") with { Delivery = ProcessorOutcomeDeliveries.Refused }, CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/handoffs/h-1/outcome/send-again", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorOutcomeAsync("h-1", CancellationToken.None);
        Assert.Equal(ProcessorOutcomeDeliveries.GivenUp, stored?.Delivery);
        Assert.Contains("was removed", stored?.ProcessorSaid);
    }

    private static ProcessorOutcomeRecord Owed(string handoffId)
        => new(
            handoffId,
            "Weir",
            "library-1",
            "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
            ProcessorOutcomeRecord.Imported,
            Path.Combine(Path.GetTempPath(), "Films", "The Terror (1963).mkv"),
            null,
            Now.AddMinutes(-5),
            ProcessorOutcomeDeliveries.Pending,
            1,
            Now.AddMinutes(-5),
            Now.AddMinutes(-4),
            false,
            "Deluno could not reach Weir.",
            Now.AddMinutes(-5));

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, string queueItemId)
        => Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                "library-1",
                "movies",
                "client-1",
                queueItemId,
                "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
                Path.Combine(Path.GetTempPath(), "Downloads", "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO"),
                "Weir"),
            CancellationToken.None);
}
