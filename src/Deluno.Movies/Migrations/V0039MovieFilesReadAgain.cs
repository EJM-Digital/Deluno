using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Every held film is read once more by the media probe (#584).
///
/// <para>In a library that hands downloads to a processor, the probe used to
/// replace the release's codec and audio with what the processor left, and the
/// file was then not read again. The rule is fixed, but only a fresh read
/// rebuilds those files' records under it; this queues that read. Nothing else
/// changes: the probe works through them in its usual slices.</para>
/// </summary>
public sealed class V0039MovieFilesReadAgain : SqliteSqlMigration
{
    public override int Version => 39;

    public override string Name => "movie_files_read_again";

    protected override string Sql => "UPDATE movie_wanted_state SET facts_probed_utc = NULL WHERE has_file = 1;";
}
