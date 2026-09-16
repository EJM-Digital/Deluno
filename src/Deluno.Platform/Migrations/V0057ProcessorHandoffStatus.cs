using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Deluno asks the processor what is happening to a file instead of timing it
/// out (#511).
///
/// <para><b>The columns</b> hold the processor's latest answer: its state, queue
/// position, when a scheduled run is due, when its state last changed, and
/// whether it has been answering at all.</para>
///
/// <para><b>The limit changes meaning.</b> <c>processor_timeout_minutes</c> was
/// "give up six hours after hand-off", which the owner rejected: a processor
/// with an overnight schedule or a queue of 4K files legitimately holds a file
/// longer than that. It is now "give up after this long with no change", in the
/// 24-hour to 7-day range he chose, with 48 hours as the default. Rows still on
/// the old six-hour default move to 48 hours; anything else set by hand is
/// brought into range.</para>
/// </summary>
public sealed class V0057ProcessorHandoffStatus : SqliteSqlMigration
{
    public override int Version => 57;

    public override string Name => "processor_handoff_status";

    protected override string Sql =>
        """
        ALTER TABLE processor_handoffs ADD COLUMN processor_state TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_message TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_queue_position INTEGER NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_scheduled_for_utc TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_last_changed_utc TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_checked_utc TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_unreachable_since_utc TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_answering_since_utc TEXT NULL;

        UPDATE libraries SET processor_timeout_minutes = 2880 WHERE processor_timeout_minutes = 360;
        UPDATE libraries SET processor_timeout_minutes = 1440 WHERE processor_timeout_minutes < 1440;
        UPDATE libraries SET processor_timeout_minutes = 10080 WHERE processor_timeout_minutes > 10080;
        """;
}
