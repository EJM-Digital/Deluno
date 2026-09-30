using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1081: a grab used to record its whole search plan, every candidate every
/// indexer returned, three times over: on the dispatch as its notes, on the
/// dispatch again as its grab response, and on the activity row about it. On
/// the lab rig that was 800 KB a copy, so the Transfers and Activity screens
/// downloaded a megabyte and a half per grab on every load.
///
/// <para>New grabs are written trimmed and once (see
/// <c>DispatchNotesTrimmer</c>). This brings the rows already on disk into line:
/// the second copy goes, and a plan of hundreds of candidates keeps its first ten
/// with a count of how many there were. Rows that are already small are not
/// touched, and no row that is not JSON is.</para>
/// </summary>
public sealed class V0040TrimOversizedGrabNotes : SqliteSqlMigration
{
    public override int Version => 40;

    public override string Name => "trim_oversized_grab_notes";

    protected override string Sql =>
        """
        -- The second copy: the grab response was the notes, byte for byte.
        UPDATE download_dispatches
        SET grab_response_json = NULL
        WHERE grab_response_json IS NOT NULL
          AND grab_response_json = notes_json;

        -- A response that was not a copy but is still enormous: cut its candidate list.
        UPDATE download_dispatches
        SET grab_response_json = json_set(
                json_remove(grab_response_json, '$.searchPlan.candidates'),
                '$.searchPlan.candidates',
                json((SELECT json_group_array(json(value)) FROM (SELECT value FROM json_each(grab_response_json, '$.searchPlan.candidates') LIMIT 10))),
                '$.searchPlan.candidatesTotal',
                json_array_length(grab_response_json, '$.searchPlan.candidates'))
        WHERE octet_length(grab_response_json) > 65536
          AND json_valid(grab_response_json)
          AND json_type(grab_response_json, '$.searchPlan.candidates') = 'array';

        UPDATE download_dispatches
        SET notes_json = json_set(
                json_remove(notes_json, '$.searchPlan.candidates'),
                '$.searchPlan.candidates',
                json((SELECT json_group_array(json(value)) FROM (SELECT value FROM json_each(notes_json, '$.searchPlan.candidates') LIMIT 10))),
                '$.searchPlan.candidatesTotal',
                json_array_length(notes_json, '$.searchPlan.candidates'))
        WHERE octet_length(notes_json) > 65536
          AND json_valid(notes_json)
          AND json_type(notes_json, '$.searchPlan.candidates') = 'array';

        -- The activity row about the grab carried the same plan.
        UPDATE activity_events
        SET details_json = json_set(
                json_remove(details_json, '$.searchPlan.candidates'),
                '$.searchPlan.candidates',
                json((SELECT json_group_array(json(value)) FROM (SELECT value FROM json_each(details_json, '$.searchPlan.candidates') LIMIT 10))),
                '$.searchPlan.candidatesTotal',
                json_array_length(details_json, '$.searchPlan.candidates'))
        WHERE octet_length(details_json) > 65536
          AND json_valid(details_json)
          AND json_type(details_json, '$.searchPlan.candidates') = 'array';
        """;
}
