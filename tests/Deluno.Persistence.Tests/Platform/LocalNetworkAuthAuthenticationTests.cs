using System.Net;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Security;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// "Skip sign-in for devices on the local network" (#993), end to end
/// through <see cref="UserAuthorization.RequireAuthenticatedAsync(HttpContext, ISecurityRepository, CancellationToken)"/>
/// -- the same entry point every endpoint and the authentication handler
/// itself call.
/// </summary>
public sealed class LocalNetworkAuthAuthenticationTests
{
    private sealed class FakeSettingsCache(LocalNetworkAuthSettings settings) : ILocalNetworkAuthSettingsCache
    {
        public Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public void Invalidate()
        {
        }
    }

    [Fact]
    public async Task Off_by_default_never_skips_sign_in_even_from_a_local_address()
    {
        var denied = await AuthenticateAsync(
            LocalNetworkAuthSettings.Off,
            remoteAddress: "192.168.1.50");

        Assert.NotNull(denied);
    }

    [Fact]
    public async Task A_local_address_skips_sign_in_when_enabled()
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], []);

        var denied = await AuthenticateAsync(settings, remoteAddress: "192.168.1.50");

        Assert.Null(denied);
    }

    [Fact]
    public async Task A_public_address_must_still_sign_in()
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], []);

        var denied = await AuthenticateAsync(settings, remoteAddress: "8.8.8.8");

        Assert.NotNull(denied);
    }

    [Theory]
    [InlineData("fd12:3456:789a::1")] // IPv6 ULA
    [InlineData("fe80::1")] // IPv6 link-local
    public async Task IPv6_ula_and_link_local_addresses_skip_sign_in_when_enabled(string address)
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], []);

        var denied = await AuthenticateAsync(settings, remoteAddress: address);

        Assert.Null(denied);
    }

    [Fact]
    public async Task Allow_list_lets_a_listed_local_device_skip_sign_in()
    {
        var settings = new LocalNetworkAuthSettings(true, [], ["192.168.1.20"], []);

        var denied = await AuthenticateAsync(settings, remoteAddress: "192.168.1.20");

        Assert.Null(denied);
    }

    [Fact]
    public async Task Allow_list_still_requires_sign_in_from_an_unlisted_local_device()
    {
        var settings = new LocalNetworkAuthSettings(true, [], ["192.168.1.20"], []);

        var denied = await AuthenticateAsync(settings, remoteAddress: "192.168.1.21");

        Assert.NotNull(denied);
    }

    [Fact]
    public async Task Spoofed_forwarded_for_from_an_untrusted_proxy_is_ignored()
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], ["10.0.0.5"]);

        // The real connection is a public address not on the trusted-proxies
        // list, so its X-Forwarded-For claim of a local address must not be honored.
        var denied = await AuthenticateAsync(settings, remoteAddress: "8.8.8.8", forwardedFor: "192.168.1.50");

        Assert.NotNull(denied);
    }

    [Fact]
    public async Task Forwarded_for_from_a_configured_trusted_proxy_is_honored()
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], ["10.0.0.5"]);

        var denied = await AuthenticateAsync(settings, remoteAddress: "10.0.0.5", forwardedFor: "192.168.1.50");

        Assert.Null(denied);
    }

    [Theory]
    [InlineData("deluno_this-key-was-never-issued", false)]
    [InlineData("deluno_", false)]
    [InlineData(null, true)]
    public async Task A_wrong_malformed_or_revoked_key_from_a_local_address_is_treated_as_no_key_and_the_local_network_rule_decides(string? key, bool revoked)
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], []);

        var fromHome = await AuthenticateAsync(settings, remoteAddress: "192.168.1.50", apiKey: key, revokeTheKeyFirst: revoked);
        var fromTheInternet = await AuthenticateAsync(settings, remoteAddress: "8.8.8.8", apiKey: key, revokeTheKeyFirst: revoked);

        Assert.Null(fromHome);
        Assert.NotNull(fromTheInternet);
    }

    private static async Task<IResult?> AuthenticateAsync(
        LocalNetworkAuthSettings settings,
        string remoteAddress,
        string? forwardedFor = null,
        string? apiKey = null,
        bool revokeTheKeyFirst = false)
    {
        using var storage = TestStorage.Create();
        var timeProvider = TimeProvider.System;
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSecurityRepository(storage.Factory, timeProvider);
        await repository.BootstrapUserAsync(
            new BootstrapUserRequest(Username: "admin", DisplayName: "Admin", Password: "password123"),
            CancellationToken.None);

        var settingsCache = new FakeSettingsCache(settings);
        var resolver = new TrustedProxyClientAddressResolver(settingsCache);

        var services = new ServiceCollection()
            .AddSingleton<ILocalNetworkAuthSettingsCache>(settingsCache)
            .AddSingleton<ITrustedProxyClientAddressResolver>(resolver)
            .BuildServiceProvider();

        if (revokeTheKeyFirst)
        {
            var created = await repository.CreateApiKeyAsync(new CreateApiKeyRequest("Revoked", "read"), CancellationToken.None);
            apiKey = created.ApiKey;
            Assert.True(await repository.DeleteApiKeyAsync(created.Item.Id, CancellationToken.None));
        }

        var context = new DefaultHttpContext { RequestServices = services };
        if (apiKey is not null)
        {
            context.Request.Headers["X-Api-Key"] = apiKey;
        }

        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return await UserAuthorization.RequireAuthenticatedAsync(context, repository, CancellationToken.None);
    }
}
