using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Support;

internal sealed class TestStorage : IDisposable
{
    private TestStorage(string dataRoot)
    {
        DataRoot = dataRoot;
        Directory.CreateDirectory(DataRoot);
        Factory = new SqliteDatabaseConnectionFactory(
            Options.Create(new StoragePathOptions { DataRoot = DataRoot }));
    }

    public string DataRoot { get; }

    public SqliteDatabaseConnectionFactory Factory { get; }

    public static TestStorage Create()
        => new(Path.Combine(Path.GetTempPath(), "deluno-tests", Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite WAL cleanup can briefly lag on Windows; leaked temp folders are test-only.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
