using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// One row per health check, keyed by a stable id such as
/// <c>download-client:{id}</c> or <c>library:{id}:free-space</c> (#909).
///
/// <para>Every one of System &#8594; Health, the Dashboard tile, the bell and
/// outbound notifications reads this table, so a check exists in exactly one
/// place and every surface agrees. A row stays until the same id reports
/// <c>ok</c> again — snoozing hides it without pretending it passed.</para>
/// </summary>
public sealed class V0070HealthChecks : SqliteSqlMigration
{
    public override int Version => 70;

    public override string Name => "health_checks";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS health_check_results (
            id TEXT PRIMARY KEY,
            category TEXT NOT NULL,
            name TEXT NOT NULL,
            status TEXT NOT NULL,
            message TEXT NOT NULL,
            cause TEXT NULL,
            fix TEXT NULL,
            details_json TEXT NULL,
            last_run_utc TEXT NOT NULL,
            problem_since_utc TEXT NULL,
            snoozed_until_utc TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_health_check_results_category
            ON health_check_results (category);

        CREATE INDEX IF NOT EXISTS ix_health_check_results_status
            ON health_check_results (status);

        -- A setting that changes what a category of checks would find asks
        -- for those checks to run now rather than waiting up to fifteen
        -- minutes (#909). One row per category: asking twice before the
        -- runner gets to it is still one request.
        CREATE TABLE IF NOT EXISTS health_check_run_requests (
            category TEXT PRIMARY KEY,
            requested_utc TEXT NOT NULL
        );
        """;
}
