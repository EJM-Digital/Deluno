using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The series half of <c>V0014MovieWantedStatusVocabulary</c>, and the one that
/// closes the split: <c>episode_wanted_state</c> already wrote <c>covered</c>
/// while <c>series_wanted_state</c> wrote <c>waiting</c> for the same state,
/// because the episode paths built their SQL by hand and never reached the
/// normaliser the title paths went through.
///
/// It also retires <c>wanted</c>, which <c>ListEligibleWantedEpisodesAsync</c>
/// read and nothing ever wrote — the value whose absence is what
/// [#303](https://github.com/jampat000/Deluno/issues/303) is about. Any row
/// holding it means an episode Deluno should be looking for.
/// </summary>
public sealed class V0015SeriesWantedStatusVocabulary : SqliteSqlMigration
{
    public override int Version => 15;

    public override string Name => "series_wanted_status_vocabulary";

    protected override string Sql =>
        """
        UPDATE series_wanted_state
        SET wanted_status = 'covered'
        WHERE wanted_status = 'waiting';

        UPDATE episode_wanted_state
        SET wanted_status = 'covered'
        WHERE wanted_status = 'waiting';

        UPDATE episode_wanted_state
        SET wanted_status = 'missing'
        WHERE wanted_status = 'wanted';
        """;
}
