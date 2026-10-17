using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1321: the episode file-link repair changes library records, so while automatic jobs are off it clears nothing
/// and says so once in Activity; when they are switched on it does its one pass.
/// </summary>
public sealed class EpisodeFileLinkRepairObeysTheAutomaticJobsSwitchTests
{
    [Fact]
    public async Task While_automatic_jobs_are_off_no_link_is_changed_and_activity_says_why()
    {
        var run = Build(automaticJobsOn: false);

        await run.Service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => run.Activity.Invocations.Count > 0, "the service to say in Activity that automatic jobs are off");
        await run.Service.StopAsync(CancellationToken.None);

        run.Series.Verify(series => series.RepairWrongEpisodeFileLinksAsync(It.IsAny<CancellationToken>()), Times.Never);
        // Each pass asks for the line on change, so Activity keeps one while nothing changes.
        run.Activity.Verify(
            activity => activity.RecordActivityOnChangeAsync(
                AutomaticJobs_PausedCategory, It.Is<string>(message => message.Contains("Automatic jobs are off")),
                null, null, "service", EpisodeFileLinkRepairService_Key, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        run.Activity.Verify(
            activity => activity.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task When_automatic_jobs_are_on_the_pass_runs_once()
    {
        var run = Build(automaticJobsOn: true);

        await run.Service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => run.Series.Invocations.Count > 0, "the service to run its one pass");
        await run.Service.StopAsync(CancellationToken.None);

        run.Series.Verify(series => series.RepairWrongEpisodeFileLinksAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_service_waiting_for_the_switch_runs_its_pass_when_it_is_switched_on()
    {
        var run = Build(automaticJobsOn: false);
        await run.Service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => run.Activity.Invocations.Count > 0, "the service to notice that automatic jobs are off");
        run.Series.Verify(series => series.RepairWrongEpisodeFileLinksAsync(It.IsAny<CancellationToken>()), Times.Never);

        run.Switch.Value = true;
        await Wait.UntilAsync(() => run.Series.Invocations.Count > 0, "the service to run its pass once the switch is on");
        await run.Service.StopAsync(CancellationToken.None);

        run.Series.Verify(series => series.RepairWrongEpisodeFileLinksAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private const string AutomaticJobs_PausedCategory = Deluno.Platform.AutomaticJobs.PausedActivityCategory;
    private const string EpisodeFileLinkRepairService_Key = "episode-file-link-repair";

    private sealed class Switch { public volatile bool Value; }

    private sealed record Run(EpisodeFileLinkRepairService Service, Mock<ISeriesCatalogRepository> Series, Mock<IActivityFeedRepository> Activity, Switch Switch);

    private static Run Build(bool automaticJobsOn)
    {
        var flag = new Switch { Value = automaticJobsOn };
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Settings(flag.Value));
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.RepairWrongEpisodeFileLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var activity = new Mock<IActivityFeedRepository>();
        var services = new ServiceCollection()
            .AddSingleton(settings.Object).AddSingleton(series.Object).AddSingleton(activity.Object)
            .BuildServiceProvider();
        var service = new EpisodeFileLinkRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EpisodeFileLinkRepairService>.Instance)
        {
            WaitForSwitch = TimeSpan.FromMilliseconds(30)
        };
        return new Run(service, series, activity, flag);
    }

    private static PlatformSettingsSnapshot Settings(bool automaticJobsOn) => new(
        MovieRootPath: null,
        SeriesRootPath: null,
        DownloadsPath: null,
        IncompleteDownloadsPath: null,
        AutoStartJobs: automaticJobsOn,
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
        HistoryRetentionDays: 30);
}
