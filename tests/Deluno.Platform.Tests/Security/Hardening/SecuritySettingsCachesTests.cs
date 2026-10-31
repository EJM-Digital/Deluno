using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Deluno.Platform.Tests.Security.Hardening;

/// <summary>
/// The Allowed Hosts and Skip-sign-in settings are read once and kept until a save says otherwise;
/// the Allowed Hosts one can be forced off from the environment or configuration without a working screen.
/// </summary>
public sealed class SecuritySettingsCachesTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public async Task Allowed_hosts_are_read_once_and_read_again_after_a_save_invalidates_them()
    {
        var first = new AllowedHostsSettings(true, ["deluno.local"]);
        var second = new AllowedHostsSettings(true, ["other.local"]);
        var repository = new Mock<IAllowedHostsRepository>();
        repository.SetupSequence(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(first).ReturnsAsync(second);
        IAllowedHostsSettingsCache cache = new AllowedHostsSettingsCache(repository.Object, Config());

        Assert.Same(first, await cache.GetAsync(CancellationToken.None));
        Assert.Same(first, await cache.GetAsync(CancellationToken.None));
        repository.Verify(r => r.GetAsync(It.IsAny<CancellationToken>()), Times.Once);

        cache.Invalidate();
        Assert.Same(second, await cache.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_configuration_override_answers_on_any_address_without_touching_the_database()
    {
        var repository = new Mock<IAllowedHostsRepository>(MockBehavior.Strict);
        var cache = new AllowedHostsSettingsCache(repository.Object, Config(("Security:AllowedHosts:ForceDisabled", "true")));
        Assert.Same(AllowedHostsSettings.Off, await cache.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_environment_override_answers_on_any_address_without_touching_the_database()
    {
        const string name = "DELUNO_ALLOWED_HOSTS_DISABLE";
        var before = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "TRUE");
            var repository = new Mock<IAllowedHostsRepository>(MockBehavior.Strict);
            Assert.Same(AllowedHostsSettings.Off, await new AllowedHostsSettingsCache(repository.Object, Config()).GetAsync(CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
        }
    }

    [Fact]
    public async Task Skip_sign_in_for_local_devices_is_read_once_and_read_again_after_a_save()
    {
        var first = new LocalNetworkAuthSettings(true, [], [], []);
        var second = new LocalNetworkAuthSettings(false, ["10.0.0.0/8"], [], []);
        var repository = new Mock<ILocalNetworkAuthRepository>();
        repository.SetupSequence(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(first).ReturnsAsync(second);
        ILocalNetworkAuthSettingsCache cache = new LocalNetworkAuthSettingsCache(repository.Object);

        Assert.Same(first, await cache.GetAsync(CancellationToken.None));
        Assert.Same(first, await cache.GetAsync(CancellationToken.None));
        repository.Verify(r => r.GetAsync(It.IsAny<CancellationToken>()), Times.Once);

        cache.Invalidate();
        Assert.Same(second, await cache.GetAsync(CancellationToken.None));
    }
}
