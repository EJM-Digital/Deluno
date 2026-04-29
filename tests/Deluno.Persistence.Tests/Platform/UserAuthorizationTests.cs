using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class UserAuthorizationTests
{
    [Fact]
    public async Task RequireAuthenticatedAsync_rejects_expired_user_tokens()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T08:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await InitializePlatformAsync(storage, timeProvider);
        var repository = CreateRepository(storage, timeProvider);
        var user = await BootstrapUserAsync(repository);
        var dataProtectionProvider = CreateDataProtectionProvider(storage);
        var token = UserAuthorization.IssueAccessToken(
            dataProtectionProvider,
            user,
            now.AddHours(-2),
            now.AddMinutes(-1));

        var denied = await UserAuthorization.RequireAuthenticatedAsync(
            CreateHttpContext(dataProtectionProvider, timeProvider, token),
            repository,
            CancellationToken.None);

        Assert.NotNull(denied);
    }

    [Fact]
    public async Task RequireAuthenticatedAsync_rejects_user_tokens_after_password_change()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T08:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await InitializePlatformAsync(storage, timeProvider);
        var repository = CreateRepository(storage, timeProvider);
        var user = await BootstrapUserAsync(repository);
        var dataProtectionProvider = CreateDataProtectionProvider(storage);
        var token = UserAuthorization.IssueAccessToken(
            dataProtectionProvider,
            user,
            now,
            now.AddHours(1));

        Assert.Null(await UserAuthorization.RequireAuthenticatedAsync(
            CreateHttpContext(dataProtectionProvider, timeProvider, token),
            repository,
            CancellationToken.None));

        Assert.True(await repository.ChangeUserPasswordAsync(user.Id, "old-password", "new-password", CancellationToken.None));

        var denied = await UserAuthorization.RequireAuthenticatedAsync(
            CreateHttpContext(dataProtectionProvider, timeProvider, token),
            repository,
            CancellationToken.None);

        Assert.NotNull(denied);
    }

    [Fact]
    public async Task RequireAuthenticatedAsync_rejects_user_tokens_after_explicit_revocation()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T08:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await InitializePlatformAsync(storage, timeProvider);
        var repository = CreateRepository(storage, timeProvider);
        var user = await BootstrapUserAsync(repository);
        var dataProtectionProvider = CreateDataProtectionProvider(storage);
        var token = UserAuthorization.IssueAccessToken(
            dataProtectionProvider,
            user,
            now,
            now.AddHours(1));

        Assert.True(await repository.RevokeUserAccessTokensAsync(user.Id, CancellationToken.None));

        var denied = await UserAuthorization.RequireAuthenticatedAsync(
            CreateHttpContext(dataProtectionProvider, timeProvider, token),
            repository,
            CancellationToken.None);

        Assert.NotNull(denied);
    }

    private static async Task InitializePlatformAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private static SqlitePlatformSettingsRepository CreateRepository(
        TestStorage storage,
        TimeProvider timeProvider)
        => new(storage.Factory, timeProvider, TestSecretProtection.Create(storage));

    private static Task<UserItem> BootstrapUserAsync(SqlitePlatformSettingsRepository repository)
        => repository.BootstrapUserAsync(
            new BootstrapUserRequest(
                Username: "admin",
                DisplayName: "Admin User",
                Password: "old-password"),
            CancellationToken.None);

    private static IDataProtectionProvider CreateDataProtectionProvider(TestStorage storage)
        => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage.DataRoot, "auth-keys")));

    private static HttpContext CreateHttpContext(
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        string token)
    {
        var services = new ServiceCollection()
            .AddSingleton(dataProtectionProvider)
            .AddSingleton(timeProvider)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }
}
