using System.Data.Common;
using Deluno.Infrastructure.Storage;

namespace Deluno.Platform;

/// <summary>
/// The database half of the title-folder check (#1067): does any title other
/// than this one have a file under a folder? It looks at movies and shows
/// together (a movie inside a show's folder, or the reverse), ignores letter
/// case and treats "/" and "\" alike, so <c>D:\Films\Dune</c> and
/// <c>d:/films/dune</c> are the same folder.
/// </summary>
public static class TitleFolderNeighbours
{
    private static readonly char[] Separators = ['/', '\\'];

    // SQLite's lower() and COLLATE NOCASE fold ASCII only, so "Édith" and "édith" would
    // never match there. Candidate paths are read and compared in C#, where
    // OrdinalIgnoreCase folds every letter.

    /// <summary>True when <paramref name="filePath"/> lies under the folder whose normalised key is <paramref name="prefix"/> (ends in "/"), ignoring letter case of every alphabet and the kind of slash.</summary>
    public static bool IsUnder(string? filePath, string prefix)
        => filePath is not null
           && filePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> OtherTitleHasFilesUnderAsync(
        IDelunoDatabaseConnectionFactory connectionFactory,
        string? ownMovieId,
        string? ownSeriesId,
        string directory,
        CancellationToken cancellationToken)
    {
        var segments = directory.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        // Keep a leading "//" (UNC) or "/" so \\nas\films and \films stay different.
        var lead = directory.StartsWith("\\\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal)
            ? "//"
            : directory.StartsWith('/') || directory.StartsWith('\\') ? "/" : string.Empty;
        var prefix = lead + string.Join('/', segments) + "/";

        return await AnyAsync(
                   connectionFactory, DelunoDatabaseNames.Movies,
                   "SELECT file_path FROM movie_wanted_state WHERE file_path IS NOT NULL AND (@own IS NULL OR movie_id <> @own)",
                   ownMovieId, prefix, cancellationToken)
               || await AnyAsync(
                   connectionFactory, DelunoDatabaseNames.Series,
                   "SELECT file_path FROM episode_entries WHERE has_file = 1 AND file_path IS NOT NULL AND (@own IS NULL OR series_id <> @own)",
                   ownSeriesId, prefix, cancellationToken);
    }

    private static async Task<bool> AnyAsync(
        IDelunoDatabaseConnectionFactory connectionFactory,
        string databaseName,
        string sql,
        string? ownId,
        string prefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(databaseName, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            Add(command, "@own", ownId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (IsUnder(reader.GetString(0), prefix))
                {
                    return true;
                }
            }

            return false;
        }
        catch (DbException exception) when (exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // That kind of title has never been set up on this install, so it holds no files.
            return false;
        }
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
