using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.SystemReadings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests.Infrastructure;

/// <summary>The small edges of the infrastructure project that no larger test walks through (#1268).</summary>
public sealed class SmallInfrastructureGapsTests
{
    [Fact]
    public void A_data_root_left_blank_is_the_data_folder_beside_the_program()
    {
        var content = Path.Combine(Path.GetTempPath(), "deluno-content-root");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new HostingEnvironment { ContentRootPath = content, EnvironmentName = "Test" });
        services.AddDelunoInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(Path.Combine(content, "data"), provider.GetRequiredService<IOptions<StoragePathOptions>>().Value.DataRoot);
    }

    [Fact]
    public void A_configured_data_root_is_made_absolute_against_the_content_root()
    {
        var content = Path.Combine(Path.GetTempPath(), "deluno-content-root");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new HostingEnvironment { ContentRootPath = content, EnvironmentName = "Test" });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{StoragePathOptions.SectionName}:DataRoot"] = "elsewhere" })
            .Build();
        services.AddDelunoInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(Path.Combine(content, "elsewhere"), provider.GetRequiredService<IOptions<StoragePathOptions>>().Value.DataRoot);
    }

    [Fact]
    public void A_system_is_named_with_its_version_only_when_it_has_one()
    {
        Assert.Equal("Windows 11 (10.0.26300)", new OperatingSystemReading("Windows 11", "10.0.26300").Describe());
        Assert.Equal("Debian", new OperatingSystemReading("Debian", null).Describe());
        Assert.Equal("Debian", new OperatingSystemReading("Debian", "  ").Describe());
    }

    [Fact]
    public void No_disks_read_is_an_empty_snapshot()
    {
        Assert.Empty(DiskSnapshot.Empty.Volumes);
        Assert.Null(DiskSnapshot.Empty.Machine);
        Assert.Equal("unknown", DriveHealthReading.Unknown.Health);
    }
}
