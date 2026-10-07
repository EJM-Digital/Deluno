using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>#1144: "Processor on RIG" becomes "Weir on RIG" once the processor publishes a product; a name someone typed never changes.</summary>
public sealed class AProcessorNamedGenericallyTakesItsOwnNameTests
{
    private sealed class Service(string? product) : IProcessorConnectionService
    {
        public Task<string?> GetProductNameAsync(string submissionUrl, string? authHeaderName, string? secret, CancellationToken cancellationToken)
            => Task.FromResult(product);

        public Task<ProcessorConnectionTestResult> TestAsync(ProcessorConnectionItem connection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProcessorSubmissionResult> SubmitAsync(ProcessorConnectionItem connection, ProcessorHandoffItem handoff, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProcessorStatusSupport> GetStatusSupportAsync(ProcessorConnectionItem connection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProcessorHandoffStatusResult> GetHandoffStatusAsync(ProcessorConnectionItem connection, string handoffId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProcessorHandoffCancelResult> CancelHandoffAsync(ProcessorConnectionItem connection, string handoffId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProcessorOutcomeResult> ReportOutcomeAsync(ProcessorConnectionItem connection, string handoffId, ProcessorHandoffOutcome outcome, string? importedPath, string? reason, DateTimeOffset occurredUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("Processor on RIG", "Weir", "Weir on RIG")]
    [InlineData("Processor on RIG", null, "Processor on RIG")]
    [InlineData("My Weir box", "Weir", "My Weir box")]
    [InlineData("Weir on RIG", "Other", "Weir on RIG")]
    public async Task Only_the_generated_name_is_renamed_and_only_when_the_processor_names_itself(string stored, string? product, string expected)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var created = await repository.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest(null, "generic-webhook", "http://192.168.1.5:8788/api/v1/intake/webhook/deluno", "X-Webhook-Secret", "s", true) { MachineName = "RIG" },
            CancellationToken.None);
        // Put the stored name in the shape under test, as an old install or a typed name would have it.
        await using (var db = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            await using var command = db.CreateCommand();
            command.CommandText = "UPDATE processor_connections SET name = @name WHERE id = @id";
            var n = command.CreateParameter(); n.ParameterName = "@name"; n.Value = stored; command.Parameters.Add(n);
            var i = command.CreateParameter(); i.ParameterName = "@id"; i.Value = created.Id; command.Parameters.Add(i);
            await command.ExecuteNonQueryAsync();
        }

        var current = (await repository.GetProcessorConnectionAsync(created.Id, CancellationToken.None))!;
        var after = await ProcessorAutoNames.RenameIfGeneratedAsync(current, repository, new Service(product), CancellationToken.None);

        Assert.Equal(expected, after.Name);
        Assert.Equal(expected, (await repository.GetProcessorConnectionAsync(created.Id, CancellationToken.None))!.Name);
    }
}
