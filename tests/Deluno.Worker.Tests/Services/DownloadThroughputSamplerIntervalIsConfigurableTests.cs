using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #823: the throughput sampler used to poll on a fixed one-minute
/// <c>PeriodicTimer</c>, with no setting to change it. It now reads
/// <see cref="PlatformSettingsSnapshot.DownloadThroughputSamplerIntervalMinutes"/>
/// fresh every tick, so a changed value takes effect on the very next sample
/// without a restart, clamped to the same 1-60 minute range the settings
/// screen enforces.
/// </summary>
public sealed class DownloadThroughputSamplerIntervalIsConfigurableTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(60, 60)]
    [InlineData(0, 1)] // Clamped to the floor rather than sampling in a tight loop.
    [InlineData(120, 60)] // Clamped to the ceiling rather than going quiet for hours.
    public async Task A_configured_interval_sets_the_next_samples_wait(int configuredMinutes, int expectedMinutes)
    {
        var sampler = CreateSampler(configuredMinutes, out _);

        var interval = await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), interval);
    }

    [Fact]
    public async Task A_sample_is_still_recorded_at_the_configured_cadence()
    {
        var sampler = CreateSampler(15, out var throughputRepository);

        await sampler.SampleAsync(CancellationToken.None);

        throughputRepository.Verify(
            repository => repository.RecordSampleAsync(It.IsAny<DownloadThroughputSample>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static DownloadThroughputSampler CreateSampler(int configuredMinutes, out Mock<IDownloadThroughputRepository> throughputRepository)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformSettingsSnapshot(
                MovieRootPath: null,
                SeriesRootPath: null,
                DownloadsPath: null,
                IncompleteDownloadsPath: null,
                AutoStartJobs: true,
                EnableNotifications: true,
                RenameOnImport: true,
                UseHardlinks: true,
                CleanupEmptyFolders: true,
                MovieFolderFormat: "{Movie}",
                SeriesFolderFormat: "{Series}",
                MovieFileFormat: "{Movie}",
                EpisodeFileFormat: "{Episode}",
                HostBindAddress: "127.0.0.1",
                HostPort: 7979,
                UrlBase: string.Empty,
                RequireAuthentication: false,
                UiTheme: "dark",
                DefaultMovieView: "poster",
                DefaultShowView: "poster",
                MetadataCertificationCountry: "US",
                MetadataLanguage: "en",
                MetadataProviderMode: "auto",
                MetadataBrokerUrl: string.Empty,
                MetadataBrokerConfigured: false,
                MetadataTmdbApiKeyConfigured: false,
                MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty,
                SearchScoringMode: "balanced",
                ImportRecoveryRetentionDays: 30,
                UpdatedUtc: DateTimeOffset.UnixEpoch,
                LibraryFreeSpaceWarnPercent: 10,
                LibraryFreeSpacePausePercent: 2,
                DownloadThroughputSamplerIntervalMinutes: configuredMinutes));

        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(service => service.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadTelemetryOverview(
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [],
                DateTimeOffset.UtcNow));

        var repository = new Mock<IDownloadThroughputRepository>();
        repository.Setup(repo => repo.RecordSampleAsync(It.IsAny<DownloadThroughputSample>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        throughputRepository = repository;

        var services = new ServiceCollection();
        services.AddSingleton(settings.Object);
        services.AddSingleton(telemetry.Object);
        services.AddSingleton(repository.Object);
        var provider = services.BuildServiceProvider();

        return new DownloadThroughputSampler(
            NullLogger<DownloadThroughputSampler>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);
    }
}
