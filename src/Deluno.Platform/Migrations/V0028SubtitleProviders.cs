using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Where a subtitle comes from, stored the same way an indexer is.
///
/// <para><b>The columns are deliberately the indexer's columns.</b> DESIGN-002
/// rule 4: providers are Connections, not a parallel registry. Health, the
/// consecutive-failure count, the rate-limit clock, the disabled reason and the
/// latency of the last test all already exist, are already correct, and are
/// already what "needs you" reads. A <c>subtitle_providers</c> table with its own
/// idea of "is this source working" would be a second answer to a question
/// Deluno has answered once — which is the AUDIT-002 defect one layer out.</para>
///
/// <para><b>Why it is a table of its own rather than rows in
/// <c>indexer_sources</c>.</b> An indexer has a base URL, a protocol, categories
/// and a sharing policy; a subtitle provider has none of those and has a
/// provider key instead — Deluno ships the client, so there is no address to
/// configure. Sharing one table would mean eight columns that are always null on
/// one kind and four that are always null on the other, which is the shape that
/// makes a reader guess. <c>download_clients</c> is its own table for exactly the
/// same reason.</para>
///
/// <para><b>Credentials are protected, not stored.</b> <c>username</c> is plain
/// because it is not a secret and a person needs to see which account is
/// configured; <c>secret</c> and <c>api_key</c> go through
/// <c>ISecretProtector</c>, the same as an indexer's API key and a download
/// client's password.</para>
/// </summary>
public sealed class V0028SubtitleProviders : SqliteSqlMigration
{
    public override int Version => 28;

    public override string Name => "subtitle_providers";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS subtitle_providers (
            id TEXT PRIMARY KEY,
            -- Which of the seven Deluno ships. The client is code, not config,
            -- so this is the whole of "which provider is this".
            provider_key TEXT NOT NULL,
            name TEXT NOT NULL,
            username TEXT NULL,
            secret TEXT NULL,
            api_key TEXT NULL,
            priority INTEGER NOT NULL DEFAULT 100,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            health_status TEXT NOT NULL DEFAULT 'untested',
            last_health_message TEXT NULL,
            last_health_latency_ms INTEGER NULL,
            last_health_test_utc TEXT NULL,
            consecutive_failures INTEGER NOT NULL DEFAULT 0,
            rate_limited_until_utc TEXT NULL,
            disabled_reason TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        -- One row per provider. Configuring the same source twice would give it
        -- two sets of credentials, two health states and two places to disable
        -- it — and another tool's registry shipped OpenSubtitles as two entries
        -- backed by one handler, which is what that looks like when nothing
        -- stops it.
        CREATE UNIQUE INDEX IF NOT EXISTS ix_subtitle_providers_key
            ON subtitle_providers (provider_key COLLATE NOCASE);

        -- The order they are asked in, and the enabled flag that decides whether
        -- they are asked at all. Both are read on every search.
        CREATE INDEX IF NOT EXISTS ix_subtitle_providers_enabled_priority
            ON subtitle_providers (is_enabled, priority, id);
        """;

    // The comment inside the SQL above was reworded when the old product name was
    // removed from the repository; installs that ran the earlier wording are adopted.
    protected override IEnumerable<string> SupersededChecksumValues =>
        ["885748222ee60ca53149604c38b3b3bcd5bcda347b3ad7b11c33ba089b8a7f00"];
}
