using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Microsoft.Extensions.Configuration;

namespace Deluno.Platform.Tests.Security.Hardening;

/// <summary>
/// #939's documented recovery path that needs no working web UI: an
/// environment variable or config key forces Allowed Hosts off regardless of
/// what is saved, without touching the saved list at all.
/// </summary>
public sealed class AllowedHostsSettingsCacheTests
{
    private sealed class FakeRepository(AllowedHostsSettings settings) : IAllowedHostsRepository
    {
        public Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveAsync(AllowedHostsSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Without_an_override_it_returns_the_saved_setting()
    {
        var cache = new AllowedHostsSettingsCache(
            new FakeRepository(new AllowedHostsSettings(true, ["my-pc"])),
            new ConfigurationBuilder().Build());

        var settings = await cache.GetAsync(CancellationToken.None);

        Assert.True(settings.Enabled);
    }

    [Fact]
    public async Task The_config_key_forces_it_off_even_though_the_saved_setting_is_on()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:AllowedHosts:ForceDisabled"] = "true" })
            .Build();
        var cache = new AllowedHostsSettingsCache(new FakeRepository(new AllowedHostsSettings(true, ["my-pc"])), config);

        var settings = await cache.GetAsync(CancellationToken.None);

        Assert.False(settings.Enabled);
    }

    [Fact]
    public async Task The_environment_variable_forces_it_off_even_though_the_saved_setting_is_on()
    {
        Environment.SetEnvironmentVariable("DELUNO_ALLOWED_HOSTS_DISABLE", "true");
        try
        {
            var cache = new AllowedHostsSettingsCache(
                new FakeRepository(new AllowedHostsSettings(true, ["my-pc"])),
                new ConfigurationBuilder().Build());

            var settings = await cache.GetAsync(CancellationToken.None);

            Assert.False(settings.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DELUNO_ALLOWED_HOSTS_DISABLE", null);
        }
    }
}
