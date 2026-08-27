using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Re-types library searches queued by an older build, so none is orphaned.
///
/// `library.search` became `library.search.movies` and `library.search.tv` so
/// each catalogue gets its own worker lane and neither can starve the other
/// ([#304](https://github.com/jampat000/Deluno/issues/304)).
///
/// **A job whose type no lane leases never runs, and nothing looks wrong** —
/// that is exactly what [#303](https://github.com/jampat000/Deluno/issues/303)
/// was, and it went unnoticed for a long time. Any row still queued or failed
/// at upgrade would sit there forever without this.
///
/// The media type is read from the job's own payload, which every enqueue site
/// writes: the planner from the library, and all three retry paths from the
/// dispatch. `series` and `tv` both appear in payloads written over the life of
/// the schema, so both are matched.
///
/// Rows that are already finished are left alone. Their type is history, and
/// rewriting history to match a name it never had would make the activity feed
/// lie about what ran.
/// </summary>
public sealed class V0017LibrarySearchPerMediaTypeJobTypes : SqliteSqlMigration
{
    public override int Version => 17;

    public override string Name => "library_search_per_media_type_job_types";

    protected override string Sql =>
        """
        UPDATE job_queue
           SET job_type = CASE
                 WHEN LOWER(COALESCE(json_extract(payload_json, '$.mediaType'), '')) IN ('tv', 'series', 'show', 'shows', 'television')
                   THEN 'library.search.tv'
                 ELSE 'library.search.movies'
               END
         WHERE job_type = 'library.search'
           AND status IN ('queued', 'failed', 'running');
        """;
}
