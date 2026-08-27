using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// One word per meaning, for [#300](https://github.com/jampat000/Deluno/issues/300).
///
/// <c>waiting</c> was set by the workflow service on a film that <em>has</em> a
/// file and already meets its target — finished, and nothing more to do. The
/// front end read the same word as "not searchable yet — it has not been
/// released", which is the opposite state, and the episode paths wrote
/// <c>covered</c> for the very same idea in raw SQL. So the word said three
/// things depending on who was reading it, and none of them could be checked
/// against the others.
///
/// <c>covered</c> is what the server always meant. <c>upcoming</c> is the state
/// that had no word at all: a film that is not out yet was stored as
/// <c>missing</c> and counted against the library from the day it was added.
/// The workflow now sets it from the release dates; this only renames what is
/// already stored.
/// </summary>
public sealed class V0014MovieWantedStatusVocabulary : SqliteSqlMigration
{
    public override int Version => 14;

    public override string Name => "movie_wanted_status_vocabulary";

    protected override string Sql =>
        """
        UPDATE movie_wanted_state
        SET wanted_status = 'covered'
        WHERE wanted_status = 'waiting';
        """;
}
