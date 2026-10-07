using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
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

    [Fact]
    public async Task RequireAuthenticatedAsync_renews_a_token_past_halfway_and_keeps_its_length()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T08:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await InitializePlatformAsync(storage, timeProvider);
        var repository = CreateRepository(storage, timeProvider);
        var user = await BootstrapUserAsync(repository);
        var dataProtectionProvider = CreateDataProtectionProvider(storage);
        var login = UserAuthorization.IssueLoginResponse(dataProtectionProvider, timeProvider, user, keepSignedIn: true);
        Assert.Equal(now.Add(UserAuthorization.KeepSignedInLifetime), login.ExpiresUtc);
        Assert.Equal(now.Add(UserAuthorization.SessionLifetime),
            UserAuthorization.IssueLoginResponse(dataProtectionProvider, timeProvider, user).ExpiresUtc);

        // Fresh token: no renewal yet.
        var fresh = CreateHttpContext(dataProtectionProvider, timeProvider, login.AccessToken);
        Assert.Null(await UserAuthorization.RequireAuthenticatedAsync(fresh, repository, CancellationToken.None));
        Assert.False(fresh.Response.Headers.ContainsKey(UserAuthorization.RenewedTokenHeader));

        // 20 days in (past halfway of 30): normal use hands back a new 30-day token.
        var later = new FixedTimeProvider(now.AddDays(20));
        var used = CreateHttpContext(dataProtectionProvider, later, login.AccessToken);
        Assert.Null(await UserAuthorization.RequireAuthenticatedAsync(used, repository, CancellationToken.None));
        var renewed = used.Response.Headers[UserAuthorization.RenewedTokenHeader].ToString();
        Assert.False(string.IsNullOrWhiteSpace(renewed));
        Assert.Equal(
            now.AddDays(50).ToString("O"),
            used.Response.Headers[UserAuthorization.RenewedTokenExpiresHeader].ToString());

        // The renewed token works after the original would have expired.
        var muchLater = new FixedTimeProvider(now.AddDays(40));
        Assert.Null(await UserAuthorization.RequireAuthenticatedAsync(
            CreateHttpContext(dataProtectionProvider, muchLater, renewed), repository, CancellationToken.None));
        Assert.NotNull(await UserAuthorization.RequireAuthenticatedAsync(
            CreateHttpContext(dataProtectionProvider, muchLater, login.AccessToken), repository, CancellationToken.None));
    }

    [Fact]
    public async Task A_renewed_token_still_ends_with_a_password_change()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T08:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await InitializePlatformAsync(storage, timeProvider);
        var repository = CreateRepository(storage, timeProvider);
        var user = await BootstrapUserAsync(repository);
        var dataProtectionProvider = CreateDataProtectionProvider(storage);
        var login = UserAuthorization.IssueLoginResponse(dataProtectionProvider, timeProvider, user);

        var later = new FixedTimeProvider(now.AddDays(5));
        var used = CreateHttpContext(dataProtectionProvider, later, login.AccessToken);
        Assert.Null(await UserAuthorization.RequireAuthenticatedAsync(used, repository, CancellationToken.None));
        var renewed = used.Response.Headers[UserAuthorization.RenewedTokenHeader].ToString();
        Assert.False(string.IsNullOrWhiteSpace(renewed));

        Assert.True(await repository.ChangeUserPasswordAsync(user.Id, "old-password", "new-password", CancellationToken.None));

        Assert.NotNull(await UserAuthorization.RequireAuthenticatedAsync(
            CreateHttpContext(dataProtectionProvider, later, renewed), repository, CancellationToken.None));
    }

    private static async Task InitializePlatformAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private static SqliteSecurityRepository CreateRepository(
        TestStorage storage,
        TimeProvider timeProvider)
        => new(storage.Factory, timeProvider);

    private static Task<UserItem> BootstrapUserAsync(SqliteSecurityRepository repository)
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
