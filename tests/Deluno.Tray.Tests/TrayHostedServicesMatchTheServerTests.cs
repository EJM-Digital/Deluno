using Deluno.Infrastructure;
using Deluno.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1324: the tray registered its own <c>ImportRecoveryCleanupService</c> with a hard-coded 30 days beside the
/// configurable one the server registers, so a person's retention days were ignored on the Windows app and two
/// clean-ups ran. The tray's hosted services must be the server's own (from the shared registrations) plus the tray's
/// updater, with no type twice and no copy of a server service kept in the tray.
/// </summary>
public sealed class TrayHostedServicesMatchTheServerTests : IDisposable
{
    /// <summary>The only hosted service that belongs to the tray itself.</summary>
    private static readonly Type[] TrayOwn = [typeof(TrayUpdateBackgroundService)];

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-tray-hosted", Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_tray_registers_no_hosted_service_twice()
    {
        var tray = HostedTypes(TrayServices());

        var twice = tray.GroupBy(type => type).Where(group => group.Count() > 1).Select(group => group.Key.Name).ToArray();
        Assert.True(twice.Length == 0, $"The tray registers these hosted services more than once: {string.Join(", ", twice)}");
    }

    [Fact]
    public void The_tray_runs_the_servers_hosted_services_and_only_its_own_updater_besides()
    {
        var tray = HostedTypes(TrayServices());
        var server = HostedTypes(ServerServices());

        var missing = server.Except(tray).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"The tray does not run these server services: {string.Join(", ", missing)}");

        var extra = tray.Except(server).Except(TrayOwn).Select(type => type.FullName).ToArray();
        Assert.True(
            extra.Length == 0,
            "The tray registers hosted services the server does not (a hard-coded copy of a server service, or a new tray " +
            $"service that must be added to this test's list of the tray's own): {string.Join(", ", extra)}");
    }

    [Fact]
    public void The_tray_keeps_no_hosted_service_class_of_its_own_beyond_the_updater()
    {
        var trayAssembly = typeof(TrayUpdateBackgroundService).Assembly;
        var copies = trayAssembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IHostedService).IsAssignableFrom(type))
            .Except(TrayOwn)
            .Select(type => type.FullName)
            .ToArray();

        Assert.True(copies.Length == 0, $"The tray assembly holds hosted services other than its updater: {string.Join(", ", copies)}");
    }

    [Fact]
    public void The_import_recovery_clean_up_is_the_servers_configurable_one()
    {
        var tray = HostedTypes(TrayServices());

        Assert.Single(tray, type => type.Name.Contains("ImportRecovery", StringComparison.Ordinal));
        Assert.Contains(typeof(Deluno.Recovery.Services.ImportRecoveryRetentionService), tray);
    }

    private ServiceCollection TrayServices()
    {
        var services = new ServiceCollection();
        DelunoServer.RegisterServices(services, Configuration(), new AppSettings { Port = 17879, DataRoot = _dataRoot }, 17879, progress: null);
        return services;
    }

    private ServiceCollection ServerServices()
    {
        // What Deluno.Host composes: the shared infrastructure and every module, nothing hosted of its own.
        var services = new ServiceCollection();
        var configuration = Configuration();
        services.AddDelunoInfrastructure(configuration);
        services.AddDelunoApplicationModules();
        return services;
    }

    private IConfiguration Configuration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataRoot"] = _dataRoot })
            .Build();

    /// <summary>The type behind each hosted-service registration, read from the descriptors without starting anything.</summary>
    private static List<Type> HostedTypes(IServiceCollection services)
    {
        var types = new List<Type>();
        foreach (var descriptor in services.Where(candidate => candidate.ServiceType == typeof(IHostedService)))
        {
            var type = descriptor.ImplementationType
                ?? descriptor.ImplementationInstance?.GetType()
                ?? FactoryProduct(descriptor);
            Assert.True(type is not null, "A hosted service is registered in a way that does not say what type it is.");
            types.Add(type!);
        }

        return types;
    }

    private static Type? FactoryProduct(ServiceDescriptor descriptor)
    {
        // AddHostedService(sp => ...) keeps the factory as Func<IServiceProvider, THostedService>.
        var factory = descriptor.ImplementationFactory;
        var arguments = factory?.GetType().GetGenericArguments();
        return arguments is { Length: 2 } && arguments[1] != typeof(object) ? arguments[1] : null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
