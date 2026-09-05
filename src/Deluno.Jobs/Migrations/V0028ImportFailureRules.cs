using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// The user's answers to the failure table, where they differ from Deluno's.
///
/// <para>DESIGN-007, James on all sixteen decisions at once: <i>"I think all
/// these things we decided need to have configuration toggles to set them on
/// and off in a management / blocklist console."</i></para>
///
/// <para><b>Only the differences are stored.</b> A missing row means the
/// shipped default, so a fresh install has an empty table and behaves exactly
/// as <c>ImportFailurePolicy</c> reads — and, more usefully, a failure kind
/// invented next year arrives with an answer instead of needing a migration
/// and a settings change. The alternative shape, seventeen columns on the
/// platform settings record, would have had to be edited every time the import
/// pipeline learned a new way to fail.</para>
/// </summary>
public sealed class V0028ImportFailureRules : SqliteSqlMigration
{
    public override int Version => 28;

    public override string Name => "import_failure_rules";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS import_failure_rules (
            reason_code TEXT PRIMARY KEY,
            decision TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """;
}
