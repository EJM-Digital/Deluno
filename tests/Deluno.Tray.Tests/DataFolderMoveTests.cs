using Deluno.Contracts;
using Deluno.Tray;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// DAT-02 (#1302): Deluno pointed at a new, empty data folder while the old one holds its data. It offers to move the data (copy,
/// check, switch) or to start fresh; nothing is deleted from the old folder and a new folder with anything in it is never written over.
/// Real folders, real files and real SQLite databases in a temp folder.
/// </summary>
public sealed class DataFolderMoveTests : IDisposable
{
    private readonly string? _local = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _common = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-tray-tests", Guid.NewGuid().ToString("N"));

    private string OldFolder => Path.Combine(_root, "old data");
    private string NewFolder => Path.Combine(_root, "new data");

    public DataFolderMoveTests()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
        AppSettings.ForgetLastRead();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _local);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _common);
        AppSettings.ForgetLastRead();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static void MakeDatabase(string path, string note)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE notes (text TEXT); INSERT INTO notes VALUES ($note);";
        command.Parameters.AddWithValue("$note", note);
        command.ExecuteNonQuery();
    }

    private static string ReadNote(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM notes";
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>An old data folder as Deluno leaves it: databases, keys, settings, logs and an empty folder.</summary>
    private void PopulateOld()
    {
        MakeDatabase(Path.Combine(OldFolder, "platform.db"), "my libraries");
        MakeDatabase(Path.Combine(OldFolder, "movies.db"), "my movies");
        Directory.CreateDirectory(Path.Combine(OldFolder, "secrets"));
        File.WriteAllText(Path.Combine(OldFolder, "secrets", "master.key"), "key bytes");
        Directory.CreateDirectory(Path.Combine(OldFolder, "logs"));
        File.WriteAllText(Path.Combine(OldFolder, "logs", "deluno.log"), "{\"line\":1}\n");
        Directory.CreateDirectory(Path.Combine(OldFolder, "protection-keys", "empty"));
    }

    private static Dictionary<string, string> Snapshot(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    private AppSettings Pointed(string? lastRoot)
    {
        Assert.Null(AppSettings.Update(s => { s.DataRoot = NewFolder; s.LastDataRoot = lastRoot; }));
        AppSettings.ForgetLastRead();
        return AppSettings.Load();
    }

    [Fact]
    public void A_new_empty_folder_while_the_old_one_holds_the_data_is_offered_a_move()
    {
        PopulateOld();
        var offer = DataFolderMove.FindOffer(Pointed(OldFolder));

        Assert.NotNull(offer);
        Assert.Equal(OldFolder, offer!.OldFolder);
        Assert.Equal(NewFolder, offer.NewFolder);
        Assert.Contains(OldFolder, DataFolderMove.QuestionWords(offer));
        Assert.Contains("Move my data here", DataFolderMove.QuestionWords(offer));
        Assert.Contains("Start fresh", DataFolderMove.QuestionWords(offer));
    }

    [Fact]
    public void A_new_folder_that_exists_but_is_empty_is_offered_a_move_too()
    {
        PopulateOld();
        Directory.CreateDirectory(NewFolder);
        Assert.NotNull(DataFolderMove.FindOffer(Pointed(OldFolder)));
    }

    [Fact]
    public void A_settings_file_from_before_the_last_folder_was_kept_offers_the_default_folder_that_holds_data()
    {
        var defaults = AppSettings.InspectPathState().DefaultDataRoot;
        MakeDatabase(Path.Combine(defaults, "platform.db"), "my libraries");

        var offer = DataFolderMove.FindOffer(Pointed(null));

        Assert.Equal(Path.GetFullPath(defaults), offer!.OldFolder);
    }

    [Fact]
    public void No_offer_when_the_old_folder_holds_no_data_only_logs_and_backups()
    {
        Directory.CreateDirectory(Path.Combine(OldFolder, "logs"));
        File.WriteAllText(Path.Combine(OldFolder, "logs", "deluno.log"), "x");
        Directory.CreateDirectory(Path.Combine(OldFolder, "backups"));
        File.WriteAllText(Path.Combine(OldFolder, "backups", "old.db"), "a backup is not the live data");

        Assert.Null(DataFolderMove.FindOffer(Pointed(OldFolder)));
    }

    [Fact]
    public void No_offer_when_the_new_folder_already_holds_anything()
    {
        PopulateOld();
        Directory.CreateDirectory(NewFolder);
        File.WriteAllText(Path.Combine(NewFolder, "notes.txt"), "someone's file");

        Assert.Null(DataFolderMove.FindOffer(Pointed(OldFolder)));
    }

    [Fact]
    public void No_offer_when_the_folder_is_the_one_Deluno_last_started_on()
    {
        PopulateOld();
        Assert.Null(DataFolderMove.FindOffer(new AppSettings { DataRoot = OldFolder, LastDataRoot = OldFolder }));
    }

    [Fact]
    public void Move_copies_everything_checks_it_and_switches_and_leaves_the_old_folder_exactly_as_it_was()
    {
        PopulateOld();
        var before = Snapshot(OldFolder);
        var offer = DataFolderMove.FindOffer(Pointed(OldFolder))!;
        var seen = new List<string>();

        var result = DataFolderMove.Move(offer, seen.Add);

        Assert.True(result.Moved, result.Words);
        Assert.Equal(before, Snapshot(NewFolder));
        Assert.True(Directory.Exists(Path.Combine(NewFolder, "protection-keys", "empty")));
        Assert.Equal("my libraries", ReadNote(Path.Combine(NewFolder, "platform.db")));
        Assert.Equal("my movies", ReadNote(Path.Combine(NewFolder, "movies.db")));
        Assert.Equal(before, Snapshot(OldFolder)); // nothing deleted, nothing changed
        Assert.Contains(seen, line => line.StartsWith("Copying your data (1 of "));
        Assert.Contains(seen, line => line.StartsWith("Checking the copy"));
        Assert.Contains("Nothing was deleted", result.Words);

        // switched: the next start is on the new folder and nothing is offered again
        AppSettings.ForgetLastRead();
        var after = AppSettings.Load();
        Assert.Equal(Path.GetFullPath(NewFolder), after.DataRoot);
        Assert.Equal(NewFolder, after.LastDataRoot);
        Assert.Null(DataFolderMove.FindOffer(after));
    }

    [Fact]
    public void Start_fresh_leaves_the_old_folder_untouched_and_is_not_asked_again()
    {
        PopulateOld();
        var before = Snapshot(OldFolder);
        var offer = DataFolderMove.FindOffer(Pointed(OldFolder))!;

        Assert.Null(DataFolderMove.StartFresh(offer));

        Assert.Equal(before, Snapshot(OldFolder));
        Assert.False(Directory.Exists(NewFolder) && Directory.EnumerateFileSystemEntries(NewFolder).Any());
        AppSettings.ForgetLastRead();
        Assert.Null(DataFolderMove.FindOffer(AppSettings.Load()));
    }

    [Fact]
    public void A_copy_that_fails_part_way_switches_nothing_removes_what_it_copied_and_says_so()
    {
        PopulateOld();
        var before = Snapshot(OldFolder);
        var offer = DataFolderMove.FindOffer(Pointed(OldFolder))!;
        var copied = 0;

        var result = DataFolderMove.Move(
            offer,
            null,
            (from, to) =>
            {
                if (++copied == 3)
                {
                    throw new IOException("The disk is full.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            },
            _ => throw new InvalidOperationException("must not switch"));

        Assert.False(result.Moved);
        Assert.Contains("The disk is full.", result.Words);
        Assert.Contains("Nothing was changed", result.Words);
        Assert.Contains(OldFolder, result.Words);
        Assert.False(Directory.Exists(NewFolder), "the folder this move made is removed again");
        Assert.Equal(before, Snapshot(OldFolder));
        AppSettings.ForgetLastRead();
        Assert.Equal(OldFolder, AppSettings.Load().LastDataRoot); // still asks next time: the choice was never made
    }

    [Fact]
    public void A_copy_that_comes_out_wrong_is_caught_by_the_check_and_switches_nothing()
    {
        PopulateOld();
        var offer = DataFolderMove.FindOffer(Pointed(OldFolder))!;

        var result = DataFolderMove.Move(
            offer,
            null,
            (from, to) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
                if (to.EndsWith("master.key", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(to, "key byteZ"); // same size, different contents
                }
            },
            _ => throw new InvalidOperationException("must not switch"));

        Assert.False(result.Moved);
        Assert.Contains("master.key", result.Words);
        Assert.Contains("contents differ", result.Words);
        Assert.False(Directory.Exists(NewFolder));
    }

    [Fact]
    public void A_database_that_does_not_open_after_the_copy_switches_nothing_and_leaves_an_empty_folder_that_was_there_empty()
    {
        PopulateOld();
        File.WriteAllText(Path.Combine(OldFolder, "broken.db"), "this is not a database at all, it is just text that is long enough");
        Directory.CreateDirectory(NewFolder);
        var offer = DataFolderMove.FindOffer(Pointed(OldFolder))!;

        var result = DataFolderMove.Move(offer, null, (from, to) => { Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to); }, _ => throw new InvalidOperationException("must not switch"));

        Assert.False(result.Moved);
        Assert.Contains("broken.db", result.Words);
        Assert.True(Directory.Exists(NewFolder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(NewFolder));
    }

    [Fact]
    public void A_new_folder_that_has_anything_in_it_is_never_written_over_even_if_it_filled_after_the_question()
    {
        PopulateOld();
        var offer = DataFolderMove.FindOffer(Pointed(OldFolder))!;
        Directory.CreateDirectory(NewFolder);
        File.WriteAllText(Path.Combine(NewFolder, "platform.db"), "somebody else's data");

        var result = DataFolderMove.Move(offer);

        Assert.False(result.Moved);
        Assert.Contains("never writes over", result.Words);
        Assert.Equal("somebody else's data", File.ReadAllText(Path.Combine(NewFolder, "platform.db")));
        Assert.Single(Directory.EnumerateFileSystemEntries(NewFolder));
    }

    [Fact]
    public void A_new_folder_inside_the_old_one_is_refused()
    {
        PopulateOld();
        var inside = new DataFolderMoveOffer(OldFolder, Path.Combine(OldFolder, "newer"));

        var result = DataFolderMove.Move(inside);

        Assert.False(result.Moved);
        Assert.Contains("inside each other", result.Words);
        Assert.False(Directory.Exists(inside.NewFolder));
    }

    [Fact]
    public void A_start_with_nobody_to_ask_does_not_start_on_the_empty_folder_and_says_what_to_do()
    {
        PopulateOld();
        Pointed(OldFolder);
        // the settings file must exist for a start to be judged
        var failure = Assert.Throws<StateFileException>(() => AppSettings.LoadForStart());

        Assert.Contains("did not start", failure.Message);
        Assert.Contains(OldFolder, failure.Message);
        Assert.Contains("Move my data here", failure.Message);
        Assert.Contains("Start fresh", failure.Message);
    }

    [Fact]
    public void A_start_remembers_the_folder_it_used_so_a_later_change_knows_where_the_data_was()
    {
        Assert.Null(AppSettings.Update(s => s.DataRoot = OldFolder));
        Directory.CreateDirectory(OldFolder);
        AppSettings.ForgetLastRead();

        var started = AppSettings.LoadForStart();

        AppSettings.ForgetLastRead();
        Assert.Equal(Path.GetFullPath(OldFolder), AppSettings.Load().LastDataRoot);
        Assert.Equal(Path.GetFullPath(OldFolder), started.DataRoot);
    }
}
