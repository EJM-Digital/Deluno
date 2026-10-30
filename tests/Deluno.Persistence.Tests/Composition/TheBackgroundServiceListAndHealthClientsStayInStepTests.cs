using Deluno.Contracts;
using Deluno.Infrastructure;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Worker;
using Deluno.Worker.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Deluno.Persistence.Tests.Composition;

/// <summary>
/// Coverage register (#1329): docs/background-services.md says "keep this list in step with the AddHostedService calls" and nothing
/// enforced it; and the two named HTTP clients the health checks use (HealthCheckHttpClients) were never read back from the real
/// registration. Both are checked here against the composition the host really builds.
/// </summary>
public sealed class TheBackgroundServiceListAndHealthClientsStayInStepTests
{
    [Fact]
    public void Every_hosted_service_the_application_registers_is_named_in_the_background_services_document()
    {
        using var storage = TestStorage.Create();
        using var provider = BuildProvider(storage);

        var document = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "background-services.md"));
        var unlisted = provider.GetServices<IHostedService>()
            .Where(service => service.GetType().Namespace?.StartsWith("Deluno", StringComparison.Ordinal) == true)
            .Select(service => service.GetType().Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !document.Contains($"`{name}`", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unlisted.Length == 0,
            $"docs/background-services.md does not list: {string.Join(", ", unlisted)}. A new service goes in the document in the same change.");
    }

    [Fact]
    public void The_two_named_health_check_clients_exist_and_have_the_short_timeouts_the_checks_rely_on()
    {
        using var storage = TestStorage.Create();
        using var provider = BuildProvider(storage);
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var systemTime = factory.CreateClient(HealthCheckHttpClients.SystemTime);
        using var proxy = factory.CreateClient(HealthCheckHttpClients.Proxy);

        Assert.Equal(TimeSpan.FromSeconds(10), systemTime.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), proxy.Timeout);
        Assert.NotEqual(HealthCheckHttpClients.SystemTime, HealthCheckHttpClients.Proxy);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Deluno.slnx) was not found above the test folder.");
    }

    private static ServiceProvider BuildProvider(TestStorage storage)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = storage.DataRoot
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, TestLifetime>();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment
        {
            ApplicationName = "Deluno",
            EnvironmentName = Environments.Production,
            ContentRootPath = storage.DataRoot
        });
        services.AddDataProtection();
        services.AddDelunoInfrastructure(configuration);
        services.AddDelunoPlatformSecrets(Path.Combine(storage.DataRoot, "secrets", "master.key"));
        services.AddDelunoApplicationModules();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
