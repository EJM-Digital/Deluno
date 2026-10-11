using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1242, from the rc.30.20 golden run (Retthree): Deluno's Activity line for the resend's outcome dropped the sentence
/// that contradicted it (#1232), but the stored outcome's <c>processorSaid</c> read "Weir recorded that Deluno imported the
/// file. Deluno will not import this file: ..." because Weir's reply carries the first send's stored note.
/// </summary>
public sealed class AStoredOutcomeKeepsOnlyWhatAgreesWithItTests
{
    private const string WeirReplyToImported =
        "Weir recorded that Deluno imported the file. Deluno will not import this file: Deluno removed Retthree.2020.1080p.WEB-DL.x264-GOLDEN.mkv from Transmission, so it will not import it. Weir kept its copy in the hand-back folder.";

    private const string WeirReplyToNotImported =
        "Weir recorded that the file will not be imported, and kept its copy. Weir recorded that Deluno imported the file.";

    private static async Task<ProcessorOutcomeRecord> SentAsync(ApplicationTestHost app, string outcome, string weirSaid)
    {
        var repository = app.Services.GetRequiredService<IProcessorRepository>();
        var created = await repository.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "generic-webhook", "http://127.0.0.1:9/hook", null, null, true),
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "processor_connections", created.Id, "Weir");
        var connections = AHandOffFollowsTheDownloadItIsForTests.Stub.Create<IProcessorConnectionService>((name, _) => name switch
        {
            "GetStatusSupportAsync" => new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { CanBeToldTheOutcome = true },
            "ReportOutcomeAsync" => new ProcessorOutcomeResult(ProcessorAnswer.Answered, false, weirSaid),
            _ => throw new NotSupportedException(name)
        });
        var record = new ProcessorOutcomeRecord(
            "h-2", "Weir", "lib", "Retthree.2020.1080p.WEB-DL.x264-GOLDEN.mkv", outcome,
            outcome == ProcessorOutcomeRecord.Imported ? "/ready/Retthree.mkv" : null,
            outcome == ProcessorOutcomeRecord.Imported ? null : "Deluno removed it, so it will not import it.",
            DateTimeOffset.UtcNow, ProcessorOutcomeDeliveries.Pending, 0, null, null, false, null, DateTimeOffset.UtcNow, created.Id);

        await ProcessorOutcomeDelivery.SendAsync(
            repository, connections, app.Services.GetRequiredService<IActivityFeedRepository>(), record, DateTimeOffset.UtcNow, CancellationToken.None);

        return (await repository.GetProcessorOutcomeAsync("h-2", CancellationToken.None))!;
    }

    [Fact]
    public async Task An_imported_outcome_stores_only_the_sentences_that_agree_with_imported()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var stored = await SentAsync(app, ProcessorOutcomeRecord.Imported, WeirReplyToImported);

        Assert.Equal(ProcessorOutcomeDeliveries.Delivered, stored.Delivery);
        Assert.Contains("Weir recorded that Deluno imported the file.", stored.ProcessorSaid);
        Assert.Contains("Weir kept its copy in the hand-back folder.", stored.ProcessorSaid);
        Assert.DoesNotContain("will not import", stored.ProcessorSaid);
    }

    [Fact]
    public async Task A_not_imported_outcome_stores_only_the_sentences_that_agree_with_not_imported()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var stored = await SentAsync(app, ProcessorOutcomeRecord.NotImported, WeirReplyToNotImported);

        Assert.Contains("will not be imported", stored.ProcessorSaid);
        Assert.DoesNotContain("Deluno imported the file", stored.ProcessorSaid);
    }

    [Fact]
    public async Task A_reply_with_nothing_that_disagrees_is_stored_as_it_was_said()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var stored = await SentAsync(app, ProcessorOutcomeRecord.Imported, "Weir recorded that Deluno imported the file. Weir kept its copy in the hand-back folder.");

        Assert.Equal("Weir recorded that Deluno imported the file. Weir kept its copy in the hand-back folder.", stored.ProcessorSaid);
    }
}
