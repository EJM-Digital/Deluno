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
        // Microsoft.Data.Sqlite pools its connections, so closing one does not
        // release the file. Without this the delete below fails on Windows and
        // the folder is silently left behind - and it is not test-only: 139,891
        // of them had accumulated under %TEMP%\deluno-tests by 3 September,
        // and creating one more directory in there is slow enough that the
        // persistence suite stopped finishing.
        //
        // Only this test's own pools. This used to be ClearAllPools(), which
        // closed the pooled connections of every other test running in
        // parallel as well - a connection disposed out from under a query in
        // another class, seen as ObjectDisposedException on a sqlite3 handle.
        Factory.ClearPools();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (Directory.Exists(DataRoot))
                {
                    Directory.Delete(DataRoot, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20 * attempt); // reason: back-off while Windows releases a file handle, three tries at most
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(20 * attempt); // reason: back-off while Windows releases a file handle, three tries at most
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
