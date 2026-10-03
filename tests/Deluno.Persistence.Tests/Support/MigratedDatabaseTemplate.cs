using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// Deluno's databases with every migration already applied, built once per test
/// run and copied as plain files into each test's own data root (#1121).
///
/// <para>Booting the application used to create and migrate every SQLite
/// database from nothing, for each of the hundreds of API tests, and that was
/// most of the suite's twenty minutes. The migrations are the same every time,
/// so they run once, here, in a process-wide template. Each test still gets
/// its own private copy of the files: nothing is shared once it starts, so one
/// test cannot see or change another's data.</para>
///
/// <para>The template lives only as long as the test process, so it is rebuilt
/// from the migrations in the assemblies under test on every run. A new or
/// changed migration therefore needs no cache key and can never be hidden by a
/// stale template. A host started on the copy still runs its schema
/// initializers, which compare every applied migration's name and checksum
/// with the code and would refuse a template that did not match.</para>
/// </summary>
internal static class MigratedDatabaseTemplate
{
    private static readonly Lazy<Task<string>> Template = new(BuildAsync);

    static MigratedDatabaseTemplate()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteQuietly();
    }

    public static async Task CopyIntoAsync(string dataRoot)
    {
        var template = await Template.Value;
        CopyDirectory(template, dataRoot);
    }

    private static async Task<string> BuildAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "deluno-tests",
            $"template-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        await ApplicationTestHost.MigrateEmptyDataRootAsync(root);

        // Fold each write-ahead log into its database so the copy is the
        // database file alone, then let go of the pooled connections.
        var factory = new SqliteDatabaseConnectionFactory(
            Options.Create(new StoragePathOptions { DataRoot = root }));
        foreach (var database in DelunoStorageLayout.Databases)
        {
            await using var connection = await factory.OpenConnectionAsync(database.Key);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync();
        }

        factory.ClearPools();

        foreach (var leftover in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(leftover);
        }

        return root;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void DeleteQuietly()
    {
        try
        {
            if (Template.IsValueCreated && Template.Value.IsCompletedSuccessfully)
            {
                Directory.Delete(Template.Value.Result, recursive: true);
            }
        }
        catch (Exception)
        {
            // Left in the temp folder; nothing depends on it.
        }
    }
}
