using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Monitoring;
using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.SystemReadings;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Dashboard;
using Deluno.Worker.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.SystemReadings;

/// <summary>What Deluno knows about itself that is not a reading of the machine (#1098).</summary>
public sealed record InstanceFacts(
    string Version,
    bool Latest,
    string Channel,
    DateTimeOffset StartedUtc,
    string Address,
    string RunsAs,
    int WorkersBusy,
    double? OldestWaitingSeconds,
    int JobsToday,
    int JobsFailedToday,
    long DataBytes,
    int DatabaseCount,
    DateTimeOffset? LastBackupUtc,
    long? LastBackupBytes,
    int RestartsThisWeek,
    int BrowsersLive,
    bool Internet,
    string? NasHost,
    Uri? MetadataUrl);

/// <summary>A drive that holds libraries, downloads or Deluno's data, before its activity and health are added.</summary>
public sealed record DriveFacts(
    string VolumeKey,
    string Name,
    string Path,
    long TotalBytes,
    long FreeBytes,
    long DelunoBytes,
    double? FullInDays);

/// <summary>Where the System overview gets what is not read off the machine: the stored records and Deluno's own settings. Tests use a fake.</summary>
public interface ISystemOverviewFacts
{
    Task<InstanceFacts> ReadInstanceAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DriveFacts>> ReadDrivesAsync(CancellationToken cancellationToken);
}

/// <summary>The real source: stored records, settings, the update checker, backups and the volume reading the Dashboard already keeps.</summary>
public sealed class SystemOverviewFacts(
    IServiceScopeFactory scopeFactory,
    DashboardVolumeCache volumes,
    JobLaneStateBoard lanes,
    IOptions<StoragePathOptions> storage,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IRealtimeWatchers? watchers = null,
    IServer? server = null) : ISystemOverviewFacts
{
    private static readonly DateTimeOffset ProcessStarted = ReadProcessStart();

    public async Task<InstanceFacts> ReadInstanceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var now = timeProvider.GetUtcNow();
        var localMidnight = new DateTimeOffset(timeProvider.GetLocalNow().Date, timeProvider.GetLocalNow().Offset).ToUniversalTime();

        var updates = await sp.GetRequiredService<IUpdateOrchestrator>().GetStatusAsync(cancellationToken);
        var store = sp.GetRequiredService<ISystemOverviewStore>();
        var jobs = await store.GetJobDayStatsAsync(localMidnight, now, cancellationToken);
        var starts = await store.CountStartsSinceAsync(now.AddDays(-7), cancellationToken);

        var settings = await sp.GetRequiredService<IPlatformSettingsRepository>().GetAsync(cancellationToken);
        var libraries = await sp.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(cancellationToken);
        var nasHost = libraries
            .Select(library => NetworkChecker.NetworkHostOf(library.RootPath))
            .FirstOrDefault(host => host is not null);

        var (lastBackupUtc, lastBackupBytes) = await LastBackupAsync(sp.GetRequiredService<IDelunoBackupService>(), cancellationToken);
        var (dataBytes, databaseCount) = await Task.Run(() => MeasureDataFolder(storage.Value.DataRoot), cancellationToken);

        return new InstanceFacts(
            Version: DelunoVersion.Running,
            Latest: !updates.UpdateAvailable,
            Channel: updates.Channel,
            StartedUtc: ProcessStarted,
            Address: ListenAddressRules.Describe(
                server?.Features.Get<IServerAddressesFeature>()?.Addresses ?? [],
                configuration.GetValue<int?>("Server:Port") ?? 5099,
                DefaultRouteAdapter.Read()?.Ip,
                Environment.MachineName),
            RunsAs: RunsAsRules.Describe(IsDocker(), OperatingSystem.IsWindows(), Environment.UserInteractive, Environment.GetEnvironmentVariable("INVOCATION_ID") is not null),
            WorkersBusy: lanes.Snapshot().Sum(lane => lane.ActiveJobs),
            OldestWaitingSeconds: jobs.OldestWaitingSeconds,
            JobsToday: jobs.JobsToday,
            JobsFailedToday: jobs.JobsFailedToday,
            DataBytes: dataBytes,
            DatabaseCount: databaseCount,
            LastBackupUtc: lastBackupUtc,
            LastBackupBytes: lastBackupBytes,
            RestartsThisWeek: Math.Max(0, starts - 1),
            BrowsersLive: watchers?.ConnectionCount ?? 0,
            Internet: !sp.GetRequiredService<IConnectivityMonitor>().Current.IsOutage,
            NasHost: nasHost,
            MetadataUrl: Uri.TryCreate(settings.MetadataBrokerUrl, UriKind.Absolute, out var broker) ? broker : null);
    }

    public async Task<IReadOnlyList<DriveFacts>> ReadDrivesAsync(CancellationToken cancellationToken)
    {
        volumes.EnsureReading();
        var reading = volumes.Latest;
        if (reading is null)
        {
            return [];
        }

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var now = timeProvider.GetLocalNow();
        var libraries = await sp.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(cancellationToken);
        var movieBytes = await sp.GetRequiredService<IMovieCatalogRepository>().GetDashboardCountsAsync(cancellationToken);
        var seriesBytes = await sp.GetRequiredService<ISeriesCatalogRepository>().GetDashboardCountsAsync(cancellationToken);
        var history = await sp.GetRequiredService<ISystemOverviewStore>()
            .ListDriveSpaceAsync(DateOnly.FromDateTime(now.Date).AddDays(-7), cancellationToken);
        var dataRoot = Path.GetFullPath(storage.Value.DataRoot);
        var dataBytes = await Task.Run(() => MeasureDataFolder(dataRoot).Bytes, cancellationToken);

        var result = new List<(int Rank, DriveFacts Drive)>();
        foreach (var volume in reading.Volumes.Where(volume => volume is { Available: true, TotalBytes: not null, FreeBytes: not null }))
        {
            var libraryBytes = libraries
                .Where(library => volume.Paths.Contains(library.RootPath, StringComparer.OrdinalIgnoreCase))
                .Sum(library => movieBytes.FileBytesByLibrary.GetValueOrDefault(library.Id) + seriesBytes.FileBytesByLibrary.GetValueOrDefault(library.Id));
            var holdsData = volume.Paths.Contains(dataRoot, StringComparer.OrdinalIgnoreCase);

            var total = volume.TotalBytes!.Value;
            var free = volume.FreeBytes!.Value;
            result.Add((DriveNaming.Rank(volume.Roles), new DriveFacts(
                volume.Id,
                DriveNaming.NameOf(volume.Roles),
                DriveNaming.PathOf(volume.Paths),
                total,
                free,
                libraryBytes + (holdsData ? dataBytes : 0),
                FullInEstimator.Estimate(history.Where(day => day.VolumeKey == volume.Id).ToArray(), free, DateOnly.FromDateTime(now.Date)))));
        }

        return result.OrderBy(item => item.Rank).Select(item => item.Drive).ToArray();
    }

    private static async Task<(DateTimeOffset?, long?)> LastBackupAsync(IDelunoBackupService backups, CancellationToken cancellationToken)
    {
        var all = await backups.ListBackupsAsync(cancellationToken);
        var newest = all.OrderByDescending(backup => backup.CreatedUtc).FirstOrDefault();
        if (newest is not null)
        {
            return (newest.CreatedUtc, newest.SizeBytes);
        }

        return ((await backups.GetSettingsAsync(cancellationToken)).LastRunUtc, null);
    }

    /// <summary>The size of Deluno's data folder and how many of its databases exist.</summary>
    private static (long Bytes, int Databases) MeasureDataFolder(string dataRoot)
    {
        long bytes = 0;
        try
        {
            var root = Path.GetFullPath(dataRoot);
            if (!Directory.Exists(root))
            {
                return (0, 0);
            }

            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                bytes += file.Length;
            }

            var databases = DelunoStorageLayout.Databases.Count(database => File.Exists(Path.Combine(root, database.FileName)));
            return (bytes, databases);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (bytes, 0);
        }
    }

    private static bool IsDocker()
        => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
           || File.Exists("/.dockerenv");

    private static DateTimeOffset ReadProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}

public static class RunsAsRules
{
    /// <summary>docker in a container; on Windows a service has no desktop session, an app has; on Linux systemd marks a service.</summary>
    public static string Describe(bool docker, bool windows, bool userInteractive, bool systemdUnit)
        => docker ? "docker" : windows ? (userInteractive ? "app" : "service") : systemdUnit ? "service" : "app";
}

public static class ListenAddressRules
{
    private static readonly string[] Wildcards = ["[::]", "0.0.0.0", "+", "*"];

    /// <summary>
    /// The address Deluno answers on, written the way a person types it. A listener on every interface
    /// ("http://[::]:5099") is shown as the machine's address on the network, or its name when it has none.
    /// </summary>
    public static string Describe(IEnumerable<string> bound, int configuredPort, string? lanIp, string machineName)
    {
        var fallbackHost = lanIp ?? machineName;
        var address = bound.FirstOrDefault();
        var schemeEnd = address?.IndexOf("://", StringComparison.Ordinal) ?? -1;
        if (address is null || schemeEnd < 0)
        {
            return $"http://{fallbackHost}:{configuredPort}";
        }

        var scheme = address[..schemeEnd];
        var authority = address[(schemeEnd + 3)..].TrimEnd('/');
        var colon = authority.LastIndexOf(':');
        var host = colon > 0 ? authority[..colon] : authority;
        var port = colon > 0 && int.TryParse(authority[(colon + 1)..], out var parsed) ? parsed : configuredPort;
        return $"{scheme}://{(Wildcards.Contains(host) ? fallbackHost : host)}:{port}";
    }
}

public static class DriveNaming
{
    private static readonly (string Role, string Label)[] Order =
    [
        ("data", "Deluno data"),
        ("library", "Library"),
        ("downloads", "Downloads"),
        ("processor", "Processor output")
    ];

    /// <summary>"Library", "Downloads" or "Deluno data"; a drive that holds several says so ("Library and downloads").</summary>
    public static string NameOf(IReadOnlyCollection<string> roles)
    {
        var labels = Order.Where(entry => roles.Contains(entry.Role)).Select(entry => entry.Label).ToArray();
        return labels.Length switch
        {
            0 => "Drive",
            1 => labels[0],
            _ => labels[0] + string.Concat(labels[1..^1].Select(label => ", " + label.ToLowerInvariant())) + " and " + labels[^1].ToLowerInvariant()
        };
    }

    /// <summary>Where a drive sits in the list: libraries first, then downloads, processor output, and Deluno's own data last.</summary>
    public static int Rank(IReadOnlyCollection<string> roles)
        => roles.Contains("library") ? 0 : roles.Contains("downloads") ? 1 : roles.Contains("processor") ? 2 : 3;

    /// <summary>The folder to show for the drive: the first one that lives on it.</summary>
    public static string PathOf(IReadOnlyList<string> paths)
        => paths.Count > 0 ? paths[0] : string.Empty;
}

public static class FullInEstimator
{
    /// <summary>The longest forecast worth showing: further out than this the drive is, for any practical purpose, not filling.</summary>
    public const double LongestForecastDays = 3650;

    /// <summary>
    /// How many days until a drive is full, at the pace of the last seven days of daily free-space readings
    /// plus today's. Null when there are less than two days to measure, when the drive is not filling, or
    /// when it would take longer than <see cref="LongestForecastDays"/>.
    /// </summary>
    public static double? Estimate(IReadOnlyList<DriveSpaceDay> readings, long freeNowBytes, DateOnly today)
    {
        var days = readings
            .Where(reading => reading.Day <= today)
            .GroupBy(reading => reading.Day)
            .Select(group => (Day: group.Key, Free: group.Last().FreeBytes))
            .OrderBy(reading => reading.Day)
            .ToList();

        // Today's own reading is the newest free space, whatever was stored earlier today.
        days.RemoveAll(reading => reading.Day == today);
        days.Add((today, freeNowBytes));

        if (days.Count < 2)
        {
            return null;
        }

        var span = days[^1].Day.DayNumber - days[0].Day.DayNumber;
        if (span < 1)
        {
            return null;
        }

        var lost = days[0].Free - days[^1].Free;
        if (lost <= 0)
        {
            return null;
        }

        var perDay = lost / (double)span;
        var daysLeft = freeNowBytes / perDay;
        return daysLeft > LongestForecastDays ? null : Math.Round(daysLeft, 1);
    }
}
