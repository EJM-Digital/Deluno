using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests;

/// <summary>The cache database is migrated at start; the startup gate hears whether that worked, and a failure is not swallowed.</summary>
public sealed class CacheSchemaInitializerTests
{
    private static (CacheSchemaInitializer Initializer, Mock<IDelunoDatabaseMigrator> Migrator, Mock<IDelunoStartupGate> Gate) Build(bool withGate = true)
    {
        var migrator = new Mock<IDelunoDatabaseMigrator>();
        var gate = new Mock<IDelunoStartupGate>();
        var factory = new Mock<IDelunoDatabaseConnectionFactory>();
        factory.Setup(f => f.GetDatabasePath(DelunoDatabaseNames.Cache)).Returns("/data/cache.db");
        return (new CacheSchemaInitializer(factory.Object, migrator.Object, NullLogger<CacheSchemaInitializer>.Instance, withGate ? gate.Object : null), migrator, gate);
    }

    [Fact]
    public async Task A_migration_that_works_marks_the_cache_database_ready()
    {
        var (initializer, migrator, gate) = Build();

        await initializer.StartAsync(CancellationToken.None);

        migrator.Verify(m => m.ApplyAsync(DelunoDatabaseNames.Cache, It.IsAny<IReadOnlyList<IDelunoDatabaseMigration>>(), It.IsAny<CancellationToken>()), Times.Once);
        gate.Verify(g => g.MarkReady(DelunoDatabaseNames.Cache), Times.Once);
        gate.Verify(g => g.MarkFailed(It.IsAny<string>(), It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task A_migration_that_fails_is_told_to_the_gate_and_thrown_on()
    {
        var (initializer, migrator, gate) = Build();
        var failure = new InvalidOperationException("disk full");
        migrator.Setup(m => m.ApplyAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IDelunoDatabaseMigration>>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.StartAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
        gate.Verify(g => g.MarkFailed(DelunoDatabaseNames.Cache, failure), Times.Once);
        gate.Verify(g => g.MarkReady(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Without_a_startup_gate_it_still_migrates_and_still_fails_loudly()
    {
        var (ok, _, _) = Build(withGate: false);
        await ok.StartAsync(CancellationToken.None);

        var (broken, migrator, _) = Build(withGate: false);
        migrator.Setup(m => m.ApplyAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IDelunoDatabaseMigration>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("x"));
        await Assert.ThrowsAsync<IOException>(() => broken.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stopping_does_nothing()
    {
        var (initializer, _, _) = Build();

        await initializer.StopAsync(CancellationToken.None);
    }
}
