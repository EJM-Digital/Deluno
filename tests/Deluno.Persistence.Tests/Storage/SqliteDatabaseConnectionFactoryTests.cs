using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Storage;

public sealed class SqliteDatabaseConnectionFactoryTests
{
    [Fact]
    public async Task ReadOnly_connection_reads_existing_database_without_permitting_writes()
    {
        using var storage = TestStorage.Create();
        await using (var writable = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            using var create = writable.CreateCommand();
            create.CommandText = "CREATE TABLE readonly_probe (id INTEGER PRIMARY KEY);";
            await create.ExecuteNonQueryAsync();
        }

        await using var readOnly = await storage.Factory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform);
        using var read = readOnly.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM readonly_probe;";
        Assert.Equal(0L, Convert.ToInt64(await read.ExecuteScalarAsync()));

        using var write = readOnly.CreateCommand();
        write.CommandText = "INSERT INTO readonly_probe DEFAULT VALUES;";
        await Assert.ThrowsAnyAsync<DbException>(() => write.ExecuteNonQueryAsync());
    }
}
