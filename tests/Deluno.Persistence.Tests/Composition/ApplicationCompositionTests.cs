using Deluno.Contracts;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Deluno.Platform;

namespace Deluno.Persistence.Tests.Composition;

/// <summary>
/// Every hosted service Deluno registers can actually be built.
///
/// <para><b>Why this test exists.</b> The Windows installer shipped an
/// application that could not start. <c>Deluno.Host</c> composed sixteen
/// modules; the tray — which is what the installer runs — composed twelve, and
/// the four it was missing included Recovery. On launch it threw <c>Unable to
/// resolve service for type 'IDispatchRecoveryHandler' while attempting to
/// activate 'DownloadDispatchPollingService'</c> and never opened a
/// listener.</para>
///
/// <para>Nothing caught it. It compiles perfectly — a missing registration is a
/// runtime fact — and every automated check drives <c>Deluno.Host</c>, the one
/// binary that had the complete list. The defect could only be seen by
/// installing the thing and watching it fail, which is what finally happened,
/// on #81.</para>
///
/// <para>Composition now lives in one place, so the drift cannot recur. This
/// holds the other half: that the one place resolves. A module registering a
/// service whose dependency nobody provides fails here rather than on somebody's
/// desktop.</para>
/// </summary>
public sealed class ApplicationCompositionTests
{
    [Fact]
    public void Every_hosted_service_the_application_registers_can_be_constructed()
    {
        using var storage = TestStorage.Create();
        using var provider = BuildProvider(storage);

        // Resolving IHostedService builds every one of them, which is exactly
        // what the host does on startup and exactly where the installed app
        // died.
        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.NotEmpty(hosted);
        Assert.All(hosted, service => Assert.NotNull(service));
    }

    /// <summary>
    /// The specific service that took the installer down, named so a failure
    /// here reads as the thing it is rather than as "some hosted service".
    /// </summary>
    [Fact]
    public void Dispatch_recovery_resolves_because_the_media_modules_registered_their_handlers()
    {
        using var storage = TestStorage.Create();
        using var provider = BuildProvider(storage);

        Assert.NotNull(provider.GetRequiredService<IDispatchRecoveryHandler>());
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

        // The two things a real host provides that the module list does not,
        // supplied here for the same reason Deluno.Host and the tray both
        // supply them: they belong to whoever is hosting, not to a module.
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

        // Validated on build, which is what turns "nobody asked for it yet"
        // into a failure at the moment the container is created rather than
        // the moment a background service first ticks.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    /// <summary>
    /// Stands in for the lifetime the generic host owns. Nothing under test
    /// starts or stops it; it exists so the container can be validated at all.
    /// </summary>
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
