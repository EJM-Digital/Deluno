using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// A film is in the libraries a person chose (#530). Adding one used to make it
/// wanted in every movie library, so each library's schedule searched for it
/// and it could be grabbed through a library it was never meant for.
///
/// <para>Existing films are narrowed to what that behaviour cannot have been
/// chosen for: every library holding a copy stays, and a film with no copy
/// anywhere keeps only its first library (the earliest created; library ids
/// are time-ordered). A second library can be added back from the film's page.</para>
/// </summary>
public sealed class V0038MovieLibraryMembership : SqliteSqlMigration
{
    public override int Version => 38;

    public override string Name => "movie_library_membership";

    protected override string Sql =>
        """
        DELETE FROM movie_wanted_state
        WHERE has_file = 0
          AND EXISTS (
              SELECT 1 FROM movie_wanted_state held
              WHERE held.movie_id = movie_wanted_state.movie_id AND held.has_file = 1);

        DELETE FROM movie_wanted_state
        WHERE has_file = 0
          AND library_id <> (
              SELECT MIN(first.library_id) FROM movie_wanted_state first
              WHERE first.movie_id = movie_wanted_state.movie_id);
        """;
}
