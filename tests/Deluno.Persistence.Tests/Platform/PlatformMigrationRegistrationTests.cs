using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Platform.Migrations;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1031: a migration class that is written but never added to
/// <see cref="PlatformDatabaseMigrations.All"/> compiles and passes its own
/// unit test, yet never runs on a real install; V0115 briefly shipped that way. Every
/// concrete platform migration in the assembly must be registered.
/// </summary>
public sealed class PlatformMigrationRegistrationTests
{
    [Fact]
    public void Every_platform_migration_class_is_registered()
    {
        var registered = PlatformDatabaseMigrations.All.Select(migration => migration.GetType()).ToHashSet();
        var declared = typeof(PlatformDatabaseMigrations).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(IDelunoDatabaseMigration).IsAssignableFrom(type)
                && type.Namespace == typeof(PlatformDatabaseMigrations).Namespace);

        Assert.Empty(declared.Where(type => !registered.Contains(type)).Select(type => type.Name));
    }

    [Fact]
    public void Registered_platform_migration_versions_are_unique_and_ascending()
    {
        var versions = PlatformDatabaseMigrations.All.Select(migration => migration.Version).ToArray();

        Assert.Equal(versions.OrderBy(version => version).Distinct(), versions);
    }
}
