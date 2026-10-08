using System.Collections.Concurrent;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// Remembers which film, show and episode rows a write touched, noted by SQLite
/// itself at the moment the row changes (#1179). Because it listens at the
/// database, every writer is counted: an HTTP handler, an import finishing, a
/// list sync or a background job. Nothing has to remember to announce a change.
///
/// <para>Only the table and row number are kept here (a hook may not read the
/// database). A reader of this log resolves them to titles later, once a burst
/// has settled, so a 1,000-episode catalogue refresh is one note per row and one
/// announcement per show.</para>
/// </summary>
public sealed class TitleWriteSink
{
    /// <summary>Beyond this many distinct rows in one burst the detail is dropped: the reader says "many things changed".</summary>
    public const int MaxRememberedRows = 20000;

    private readonly object _gate = new();
    private readonly IReadOnlySet<string> _tables;
    private Dictionary<string, HashSet<long>> _rows = new(StringComparer.Ordinal);
    private int _count;
    private bool _overflow;

    internal TitleWriteSink(string databasePath, IReadOnlySet<string> tables)
    {
        DatabasePath = databasePath;
        _tables = tables;
    }

    public string DatabasePath { get; }

    /// <summary>Released when the first row of a burst is noted; a reader waits on it instead of polling.</summary>
    public SemaphoreSlim Signal { get; } = new(0, 1);

    internal void Record(string table, long rowId)
    {
        if (!_tables.Contains(table))
        {
            return;
        }

        bool first;
        lock (_gate)
        {
            first = _count == 0 && !_overflow;
            if (_count >= MaxRememberedRows)
            {
                _overflow = true;
            }
            else
            {
                if (!_rows.TryGetValue(table, out var set))
                {
                    set = [];
                    _rows[table] = set;
                }

                if (set.Add(rowId))
                {
                    _count++;
                }
            }
        }

        if (first)
        {
            try
            {
                Signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>Takes everything noted so far and starts a fresh burst.</summary>
    public TitleWriteBatch Take()
    {
        lock (_gate)
        {
            var batch = new TitleWriteBatch(_rows, _overflow);
            _rows = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            _count = 0;
            _overflow = false;
            return batch;
        }
    }

    /// <summary>Notes a row directly. For tests and for writers that bypass SQLite's own hook.</summary>
    public void Note(string table, long rowId) => Record(table, rowId);
}

public sealed record TitleWriteBatch(IReadOnlyDictionary<string, HashSet<long>> Rows, bool Overflow)
{
    public bool IsEmpty => Rows.Count == 0 && !Overflow;
}

/// <summary>The tables whose rows make up a film, a show and its episodes.</summary>
public static class TitleWriteTables
{
    public static readonly IReadOnlySet<string> Movies = new HashSet<string>(StringComparer.Ordinal)
    {
        "movie_entries", "movie_wanted_state", "movie_subtitle_state",
        "movie_collections", "movie_collection_members"
    };

    /// <summary>Tables whose rows are not one title's: a change to them says "films changed" without saying which.</summary>
    public static readonly IReadOnlySet<string> Unattributed = new HashSet<string>(StringComparer.Ordinal)
    {
        "movie_collections", "movie_collection_members"
    };

    public static readonly IReadOnlySet<string> Series = new HashSet<string>(StringComparer.Ordinal)
    {
        "series_entries", "series_wanted_state", "season_entries", "episode_entries",
        "episode_wanted_state", "episode_subtitle_state"
    };

    private static readonly ConcurrentDictionary<string, TitleWriteSink> Sinks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sink for one database file, or null for a database that holds no titles.</summary>
    public static TitleWriteSink? SinkFor(string databasePath, string databaseName)
    {
        var tables = string.Equals(databaseName, DelunoDatabaseNames.Movies, StringComparison.OrdinalIgnoreCase) ? Movies
            : string.Equals(databaseName, DelunoDatabaseNames.Series, StringComparison.OrdinalIgnoreCase) ? Series
            : null;
        return tables is null ? null : Sinks.GetOrAdd(databasePath, path => new TitleWriteSink(path, tables));
    }
}
