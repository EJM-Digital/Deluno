using Deluno.Infrastructure;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Deluno.Persistence.Tests.Composition;

/// <summary>
/// #1321: since 27 Sep nothing automatic may change a person's files, downloads or library records unless they switched
/// automatic jobs on. The mislabelled-episode repair did, and nothing would have caught the next one. This lists every
/// hosted service from the real registration and makes each one say which it is: a service that changes media, which
/// must carry <see cref="ChangesMediaOnlyWhenAutomaticJobsAreOnAttribute"/> and ask <see cref="AutomaticJobs.IsOnAsync"/>,
/// or one that does not, with the reason. A service that is in neither list fails, so a new one cannot slip in.
/// </summary>
public sealed class HostedServicesObeyTheAutomaticJobsSwitchTests
{
    /// <summary>Hosted services that change files, downloads or library records on their own: they ask the switch.</summary>
    private static readonly string[] ChangeMedia =
    [
        "EpisodeFileLinkRepairService",           // clears a wrong episode-to-file link
        "MislabelledEpisodeFileRepairService",    // moves a mislabelled file to the recycle bin
    ];

    /// <summary>Hosted services that do not change media, and why. Anything not here or above fails the test.</summary>
    private static readonly Dictionary<string, string> DoNotChangeMedia = new(StringComparer.Ordinal)
    {
        // Schema and one-off upgrades of Deluno's own databases (run on every start whatever the switch says).
        ["DelunoStorageBootstrapService"] = "creates Deluno's own databases",
        ["CacheSchemaInitializer"] = "creates Deluno's own cache tables",
        ["JobsSchemaInitializer"] = "creates Deluno's own job tables",
        ["MoviesSchemaInitializer"] = "creates Deluno's own movie tables",
        ["SeriesSchemaInitializer"] = "creates Deluno's own series tables",
        ["PlatformSchemaInitializer"] = "creates Deluno's own settings tables",
        ["IndexerIdentityBackfillService"] = "links Deluno's own history rows to their indexer by id",
        ["IndexerNamesUpgradeService"] = "renames indexer names inside Deluno's own history",
        ["NotificationSecretsUpgradeService"] = "protects the saved notification addresses and tokens inside Deluno's own database",
        ["CollectionExclusionMigrationHostedService"] = "one-off upgrade of Deluno's own collection settings",
        ["MovieRungRepairReportService"] = "only writes an Activity line about a repair a migration already made",
        // Deluno's own data and settings.
        ["DelunoBackupService"] = "writes a backup of Deluno's own databases, on the person's backup schedule",
        ["DatabaseIntegrityService"] = "checks Deluno's own databases",
        ["HistoryRetentionWorker"] = "prunes Deluno's own Activity history to the person's window; touches no title or file",
        ["ImportRecoveryRetentionService"] = "removes old resolved import-recovery cases from Deluno's own records, to the person's retention days",
        ["InstalledComponentWiringService"] = "forgets logins Deluno stored for tools it set up; connects nothing",
        ["RankingModelTrainingHostedService"] = "trains Deluno's own ranking model",
        ["GuideUpdateCheckHostedService"] = "checks for a newer quality guide; changes nothing until the person applies it",
        ["GuideCatalogueUpgradeHostedService"] = "upgrades Deluno's own bundled quality catalogue",
        ["TrackerDefinitionAutoUpdateHostedService"] = "refreshes Deluno's own tracker definitions",
        ["TrackerSiteSwitchHostedService"] = "switches a tracker's address in Deluno's own definitions",
        ["CloudflareHelperSessionCleanupService"] = "closes Deluno's own helper sessions",
        ["SceneMappingRefreshHostedService"] = "refreshes reference scene-numbering data",
        ["EpisodeGroupRefreshHostedService"] = "refreshes reference episode-order data",
        ["ContentBlocklistRemoteRefreshService"] = "refreshes the person's remote content blocklist, which is itself off until switched on",
        ["QbittorrentBlocklistSyncService"] = "pushes the content blocklist to qBittorrent only when its own switch is on (#936)",
        ["RetentionScheduleHostedService"] = "checks the person's own retention rules; it only marks, and nothing is removed until the person confirms",
        // Bookkeeping of downloads the person or an automatic job already started; any search it queues is run by the job lanes, which obey the switch.
        ["DownloadDispatchPollingHostedService"] = "settles and records the state of downloads Deluno already sent",
        // Read-only watchers, samplers and publishers.
        ["DownloadTelemetryRefreshService"] = "reads download client state",
        ["OrphanedDownloadRefreshService"] = "lists download folders; deletes nothing",
        ["DownloadFolderChangeNoticeService"] = "raises a notice only",
        ["DownloadThroughputSampler"] = "samples speeds",
        ["DownloadProgressPublisher"] = "pushes progress to open screens",
        ["MachineTelemetrySampler"] = "samples the machine",
        ["DashboardVolumeRefresher"] = "reads disk space",
        ["SystemOverviewSampler"] = "samples the machine",
        ["SystemOverviewRecorder"] = "records the samples",
        ["SystemLogPushService"] = "pushes log lines to open screens",
        ["SystemChangeWatcher"] = "watches for changes to push",
        ["OutboundThrottleWatcher"] = "watches request rates",
        ["SignalRRealtimeEventPublisher"] = "pushes events to open screens",
        ["TitleChangeNotifier"] = "pushes title changes to open screens",
        ["HealthCheckHostedService"] = "runs health checks",
        ["RegularConnectionCheckService"] = "checks connections",
        ["ProcessorHealthWatcher"] = "checks processors",
        ["KeepAwakeService"] = "keeps the machine awake while Deluno works",
        ["BackgroundServiceFailureMonitor"] = "watches the other services for failure",
        ["DelunoHeartbeatWorker"] = "the job lanes themselves, which stop when the switch is off",
    };

    [Fact]
    public void Every_hosted_service_is_classified_and_those_that_change_media_ask_the_switch()
    {
        var hosted = HostedTypes();
        Assert.NotEmpty(hosted);

        var unclassified = hosted
            .Where(type => !ChangeMedia.Contains(type.Name) && !DoNotChangeMedia.ContainsKey(type.Name))
            .Select(type => type.FullName)
            .ToArray();
        Assert.True(
            unclassified.Length == 0,
            "These hosted services are not classified. Say whether each changes files, downloads or library records " +
            $"(then it must ask the automatic-jobs switch), or why it does not: {string.Join(", ", unclassified)}");

        foreach (var name in ChangeMedia)
        {
            var type = hosted.SingleOrDefault(candidate => candidate.Name == name);
            Assert.True(type is not null, $"{name} is listed as changing media but is not a registered hosted service.");
            Assert.True(
                Attribute.IsDefined(type!, typeof(ChangesMediaOnlyWhenAutomaticJobsAreOnAttribute), inherit: false),
                $"{name} changes media and must carry [ChangesMediaOnlyWhenAutomaticJobsAreOn].");
            Assert.Contains("AutomaticJobs.IsOnAsync", SourceOf(type!));
        }
    }

    [Fact]
    public void A_service_listed_as_not_changing_media_is_still_registered_so_the_list_cannot_go_stale()
    {
        var names = HostedTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var stale = DoNotChangeMedia.Keys.Where(name => !names.Contains(name)).ToArray();
        Assert.True(stale.Length == 0, $"These names are no longer registered; remove them: {string.Join(", ", stale)}");
    }

    [Fact]
    public void A_service_that_carries_the_mark_is_in_the_list_of_those_that_change_media()
    {
        var marked = HostedTypes()
            .Where(type => Attribute.IsDefined(type, typeof(ChangesMediaOnlyWhenAutomaticJobsAreOnAttribute), inherit: false))
            .Select(type => type.Name);
        Assert.Equal(ChangeMedia.OrderBy(name => name), marked.OrderBy(name => name));
    }

    private static Type[] HostedTypes()
    {
        using var storage = TestStorage.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataRoot"] = storage.DataRoot })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, NoLifetime>();
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

        using var provider = services.BuildServiceProvider();
        return provider.GetServices<IHostedService>().Select(service => service.GetType())
            .Where(type => type.Namespace?.StartsWith("Deluno", StringComparison.Ordinal) == true)
            .Distinct().ToArray();
    }

    private static string SourceOf(Type type)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var marker = $"class {type.Name}";
        var file = Directory.EnumerateFiles(Path.Combine(directory!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .First(path => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal));
        return File.ReadAllText(file);
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
