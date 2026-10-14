using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// A migration that rewrites, moves, merges, renames or deletes data, or fills a new column from other rows, rather than
/// only adding tables, columns or indexes.
///
/// <para><see cref="Allowed"/> says what it may do to rows that were already there: <c>table.column</c> (that value may
/// change), <c>table.*</c> (any value may change), <c>~table</c> (rows of that table may be added or removed, or the table
/// dropped). Anything not listed must come through exactly as it was, which
/// <see cref="EveryMigrationKeepsItsRowsTests"/> checks for every migration.</para>
///
/// <para><see cref="Tests"/> names the hand-written tests that prove what the rows mean afterwards, as
/// <c>Class.MethodPrefix</c> or just <c>Class</c>; <see cref="EveryDataChangingMigrationHasItsTestTests"/> checks that each
/// one exists.</para>
/// </summary>
internal sealed record DataChange(string Database, int Version, string[] Allowed, params string[] Tests);

internal static class DataChangingMigrations
{
    private const string Platform = DelunoDatabaseNames.Platform;
    private const string Jobs = DelunoDatabaseNames.Jobs;
    private const string Movies = DelunoDatabaseNames.Movies;
    private const string Series = DelunoDatabaseNames.Series;

    private const string PlatformTests = "PlatformDataMigrationTests";
    private const string JobsTests = "JobsDataMigrationTests";
    private const string CatalogueTests = "CatalogueDataMigrationTests";
    private const string RepeatTests = "SearchHistoryAndActivityRepeatTests";

    public static IReadOnlyList<DataChange> All { get; } =
    [
        // ---- platform ----
        new(Platform, 2, ["users.security_stamp"], $"{PlatformTests}.V0002"),
        new(Platform, 3, ["indexer_sources.health_status", "download_clients.health_status"], $"{PlatformTests}.V0003"),
        new(Platform, 13, ["~quality_profiles"], $"{PlatformTests}.V0013"),
        new(Platform, 21, ["quality_profiles.allowed_qualities", "quality_profiles.cutoff_quality"], $"{PlatformTests}.V0021"),
        new(Platform, 26, ["library_views.quick_filter", "library_views.monitoring"], $"{PlatformTests}.V0026"),
        new(Platform, 33, [], $"{PlatformTests}.V0033"),
        new(Platform, 50, ["~playback_goals", "~playback_device_groups", "~playback_device_profiles"], $"{PlatformTests}.V0050"),
        new(Platform, 57, ["libraries.processor_timeout_minutes"], $"{PlatformTests}.V0057"),
        new(Platform, 66, ["~system_settings"], $"{PlatformTests}.V0066"),
        new(Platform, 73, ["~system_settings"], "AutomationDefaultsOffMigrationTests"),
        new(Platform, 76, ["~system_settings"], "SubtitleFetchDefaultsExistingMigrationTests"),
        new(Platform, 84, ["~system_settings"], "RenameFoldersOnImportDefaultsExistingMigrationTests"),
        new(Platform, 92, [], "DownloadClientRemoveCompletedFailedMigrationTests"),
        new(Platform, 93, ["~system_settings"], $"{PlatformTests}.V0093"),
        new(Platform, 96, ["~system_settings"], "SecurityAllowedHostsDefaultsOffMigrationTests"),
        new(Platform, 97, ["libraries.max_missing_items_per_run", "libraries.max_upgrade_items_per_run"], "LibraryMissingUpgradeCapsMigrationTests"),
        new(Platform, 109, ["download_client_path_mappings.host"], $"{PlatformTests}.V0109"),
        new(Platform, 113, ["~system_settings"], "ContentBlocklistDefaultsOnMigrationTests"),
        new(Platform, 115, ["~system_settings"], "RemoveDeadNfoSettingMigrationTests"),
        new(Platform, 116, ["libraries.search_interval_minutes"], "LibrarySearchIntervalMinutesMigrationTests"),
        new(Platform, 120, ["~system_settings"], "DropMetadataArtworkEnabledMigrationTests"),
        new(Platform, 121, ["~destination_rules"], $"{PlatformTests}.V0121"),
        new(Platform, 125, ["release_profiles.tag_names", "release_profiles.position"], $"{PlatformTests}.V0125"),
        new(Platform, 131, ["~system_settings"], "InstanceNameRemovedTests"),
        new(Platform, 132, [], "ProcessorLinksByIdTests"),
        new(Platform, 133, ["intake_sources.name", "download_clients.name", "media_servers.name", "notification_webhooks.name", "processor_connections.name", "seerr_servers.name", "indexer_sources.name"], "ConnectionNamesTests"),
        new(Platform, 135, ["indexer_proxies.name"], "IndexerProxyNamesTests"),
        new(Platform, 137, ["~system_settings"], "StrikeRulesDefaultOffMigrationTests"),

        // ---- jobs ----
        new(Jobs, 2, ["job_queue.next_attempt_utc", "job_queue.max_attempts"], $"{JobsTests}.V0002"),
        new(Jobs, 16, ["download_dispatches.import_status"], $"{JobsTests}.V0016"),
        new(Jobs, 17, ["job_queue.job_type"], $"{JobsTests}.V0017"),
        new(Jobs, 32, ["download_dispatches.client_completed_utc"], $"{JobsTests}.V0032"),
        new(Jobs, 33, ["~job_queue", "download_dispatches.grab_failure_json"], $"{JobsTests}.V0033"),
        new(Jobs, 40, ["download_dispatches.grab_response_json", "download_dispatches.notes_json", "activity_events.details_json"], RepeatTests),
        new(Jobs, 41, ["~activity_events", "activity_events.details_json"], RepeatTests),
        new(Jobs, 50, ["~activity_events"], "HistoryHoldsEarlierTurnedDownTests"),

        // ---- movies ----
        new(Movies, 14, ["movie_wanted_state.wanted_status"], $"{CatalogueTests}.Movies_V0014"),
        new(Movies, 16, ["movie_entries.primary_file_size_bytes", "movie_entries.primary_quality_rank"], $"{CatalogueTests}.V0016_and_V0017"),
        new(Movies, 20, ["movie_entries.status", "movie_entries.studio"], $"{CatalogueTests}.V0020_reads"),
        new(Movies, 21, ["movie_entries.sort_title"], $"{CatalogueTests}.V0021_and_V0022"),
        new(Movies, 22, ["movie_entries.*"], $"{CatalogueTests}.V0022_and_V0023"),
        new(Movies, 23, ["movie_entries.keywords"], $"{CatalogueTests}.V0023_and_V0024"),
        new(Movies, 25, ["movie_entries.*"], $"{CatalogueTests}.V0025_and_V0026"),
        new(Movies, 26, ["movie_entries.*"], $"{CatalogueTests}.V0026_and_V0027"),
        new(Movies, 27, ["movie_entries.size_conformance"], $"{CatalogueTests}.V0027_and_V0028"),
        new(Movies, 31, [], $"{CatalogueTests}.The_media_tags_migration"),
        new(Movies, 28, ["movie_entries.*"], $"{CatalogueTests}.V0028_and_V0029"),
        new(Movies, 29, ["movie_entries.*"], $"{CatalogueTests}.V0029_and_V0030"),
        new(Movies, 38, ["~movie_wanted_state"], "LibraryMembershipTests"),
        new(Movies, 39, ["movie_wanted_state.facts_probed_utc"], $"{CatalogueTests}.V0039_and_V0042"),
        new(Movies, 45, ["~movie_wanted_state", "movie_wanted_state.*"], "MovieOneRungPerMovieTests"),
        new(Movies, 47, ["~movie_wanted_state", "movie_wanted_state.*"], "MovieOneRungPerMovieTests"),
        new(Movies, 51, ["movie_search_history.details_json"], RepeatTests),

        // ---- series ----
        new(Series, 15, ["series_wanted_state.wanted_status", "episode_wanted_state.wanted_status"], $"{CatalogueTests}.Series_V0015"),
        new(Series, 17, ["series_entries.primary_file_size_bytes", "series_entries.primary_quality_rank"], $"{CatalogueTests}.V0016_and_V0017"),
        new(Series, 20, ["series_entries.*"], $"{CatalogueTests}.Series_V0020", $"{CatalogueTests}.V0020_reads"),
        new(Series, 22, ["series_entries.sort_title"], $"{CatalogueTests}.V0021_and_V0022"),
        new(Series, 23, ["series_entries.*"], $"{CatalogueTests}.V0022_and_V0023"),
        new(Series, 24, ["series_entries.keywords"], $"{CatalogueTests}.V0023_and_V0024"),
        new(Series, 26, ["series_entries.*"], $"{CatalogueTests}.V0025_and_V0026"),
        new(Series, 27, ["series_entries.*"], $"{CatalogueTests}.V0026_and_V0027"),
        new(Series, 33, [], $"{CatalogueTests}.The_media_tags_migration"),
        new(Series, 28, ["series_entries.size_conformance"], $"{CatalogueTests}.V0027_and_V0028"),
        new(Series, 29, ["series_entries.*"], $"{CatalogueTests}.V0028_and_V0029"),
        new(Series, 30, ["series_entries.*"], $"{CatalogueTests}.V0029_and_V0030"),
        new(Series, 41, ["~series_wanted_state", "episode_wanted_state.library_id"], $"{CatalogueTests}.Series_V0041"),
        new(Series, 42, ["series_wanted_state.facts_probed_utc", "episode_entries.facts_probed_utc"], $"{CatalogueTests}.V0039_and_V0042"),
        new(Series, 54, ["series_search_history.details_json"], RepeatTests)
    ];

    public static DataChange? Find(string database, int version)
        => All.SingleOrDefault(change => change.Database == database && change.Version == version);
}
