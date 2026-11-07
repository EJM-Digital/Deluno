using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1529: a processor reported a finished file that is not there. The report is answered at once and Deluno looks again
/// once the share's cached "not there" has passed, so the path it was told, and when, are kept on the hand-off in their own
/// columns: the send, the watcher's notices and every other write leave them alone, and only the hand-off ending clears them.
/// </summary>
public sealed class V0144ProcessorHandoffMissingOutput : SqliteSqlMigration
{
    public override int Version => 144;

    public override string Name => "processor_handoff_missing_output";

    protected override string Sql =>
        """
        ALTER TABLE processor_handoffs ADD COLUMN missing_output_path TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN missing_output_since_utc TEXT NULL;
        """;
}
