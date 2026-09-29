using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// What a retention rule does on a title's day, and the hand-made edges of its
/// Leaving soon list (#1008 - #1012).
///
/// <list type="bullet">
/// <item><c>expiry_action</c> - <c>delete</c> (what every rule did until now),
/// <c>unmonitor</c>, <c>log</c> or <c>tag</c>. Existing rules keep <c>delete</c>.</item>
/// <item><c>expiry_tag</c> - the tag the <c>tag</c> action gives the title.</item>
/// <item><c>keep_in_deluno_only</c> - the rule's Leaving soon list is never sent
/// to a media server as a collection. Off, so existing rules go on syncing.</item>
/// <item><c>defer_while_playing</c> - a removal waits while the title is being
/// played. Off for existing rules (#936); the screen offers it on for a new one.</item>
/// <item><c>retention_marks.is_manual</c> - a title a person added to the list by
/// hand: the rule's steps no longer decide whether it stays.</item>
/// </list>
/// </summary>
public sealed class V0130RetentionActions : SqliteSqlMigration
{
    public override int Version => 130;

    public override string Name => "retention_actions";

    protected override string Sql =>
        """
        ALTER TABLE retention_rules ADD COLUMN expiry_action TEXT NOT NULL DEFAULT 'delete';
        ALTER TABLE retention_rules ADD COLUMN expiry_tag TEXT NULL;
        ALTER TABLE retention_rules ADD COLUMN keep_in_deluno_only INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE retention_rules ADD COLUMN defer_while_playing INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE retention_marks ADD COLUMN is_manual INTEGER NOT NULL DEFAULT 0;
        """;
}
