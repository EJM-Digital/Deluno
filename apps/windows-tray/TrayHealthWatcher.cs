using System.Diagnostics;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Realtime;

namespace Deluno.Tray;

/// <summary>
/// What the tray's dot and pause bars are drawn from: the things Deluno relies on that do not answer (in the words the
/// hover text uses) and whether automation is paused.
/// </summary>
public sealed record TrayServerSignals(IReadOnlyList<string> Down, bool AutomationPaused, bool HealthUnreadable = false);

/// <summary>
/// Follows the in-process server's own pushes about platform health (HealthChanged, ConnectionChecked and the changes
/// that add, remove or switch a connection) and, when one arrives, reads the health data again. There is no timer: a
/// burst of pushes is gathered into one read after <see cref="Coalesce"/>, which is a pause for gathering, not a poll.
/// It does not read the attention summary: downloads, hand-offs and bell items are not platform health.
/// </summary>
public sealed class TrayHealthWatcher : IDisposable
{
    /// <summary>The pushes that can move a connection's health, a library folder or disk, or the Automation switch.</summary>
    public static bool Moves(string eventName) => eventName is
        LiveEventNames.ConnectionChecked
        or LiveEventNames.ClientStateChanged
        or LiveEventNames.DriveSpaceChanged
        or "HealthChanged"
        or "SettingsChanged"
        or "AutomationStateChanged"
        or "DownloadClientChanged"
        or "IndexerChanged";

    /// <summary>When this host process started; a metadata check from before it, or soon after it, is not a reading of the new run.</summary>
    private static readonly DateTimeOffset HostStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(500);

    private readonly IServiceProvider _services;
    private readonly SignalRRealtimeEventPublisher? _publisher;
    private readonly Action<TrayServerSignals> _changed;
    private readonly object _lock = new();
    private bool _scheduled;
    private bool _disposed;
    private bool _everRead;
    private bool _startChecked;

    public TrayHealthWatcher(IServiceProvider services, Action<TrayServerSignals> changed)
    {
        _services = services;
        _changed = changed;
        _publisher = services.GetService(typeof(SignalRRealtimeEventPublisher)) as SignalRRealtimeEventPublisher;
        if (_publisher is not null)
        {
            _publisher.EventPublished += OnPublished;
        }
    }

    private void OnPublished(string eventName)
    {
        if (Moves(eventName))
        {
            Schedule();
        }
    }

    /// <summary>Reads the health now (once the host is up), then follows the pushes.</summary>
    public void Schedule()
    {
        lock (_lock)
        {
            if (_scheduled || _disposed)
            {
                return;
            }

            _scheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Coalesce).ConfigureAwait(false);
            lock (_lock)
            {
                _scheduled = false;
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                if (!_startChecked)
                {
                    await CheckMetadataAfterStartAsync().ConfigureAwait(false);
                    _startChecked = true;
                }

                var signals = await ReadAsync().ConfigureAwait(false);
                _everRead = true;
                _changed(signals);
            }
            catch
            {
                // A reading that failed leaves the dot as it was; the next push reads again. Until a first reading has
                // worked the dot is amber and says so: green would claim everything answers, which is not known.
                if (!_everRead)
                {
                    _changed(new TrayServerSignals([], false, HealthUnreadable: true));
                }
            }
        });
    }

    /// <summary>How many times, and how far apart, the first metadata check is tried while the broker comes up.</summary>
    public static readonly (int Attempts, TimeSpan Gap) StartCheck = (4, TimeSpan.FromSeconds(5));

    /// <summary>
    /// Checks the metadata service once the host has started, again a few seconds on while it fails (the broker may
    /// still be coming up), and records each result like any check, which pushes ConnectionChecked. The tray keeps
    /// blinking until this is done, then shows what the fresh result says, so an outage right after a start still shows.
    /// </summary>
    private async Task CheckMetadataAfterStartAsync()
    {
        for (var attempt = 1; attempt <= StartCheck.Attempts && !_disposed; attempt++)
        {
            try
            {
                using var scope = _services.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var tester = scope.ServiceProvider.GetService<IConnectionHealthTester>();
                if (tester is null)
                {
                    return;
                }

                var result = await tester.TestAsync(ConnectionKinds.Metadata, "metadata", CancellationToken.None).ConfigureAwait(false);
                if (result is null || result.Ok)
                {
                    return;
                }
            }
            catch
            {
                // A check that could not run leaves the old record; the reading below ignores a pre-start one.
            }

            if (attempt < StartCheck.Attempts)
            {
                await Task.Delay(StartCheck.Gap).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The connections and folders that do not answer, and the Automation switch, read in-process.</summary>
    public async Task<TrayServerSignals> ReadAsync()
    {
        using var scope = _services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var sp = scope.ServiceProvider;
        var connections = sp.GetRequiredService<IConnectionsRepository>();
        var processors = sp.GetRequiredService<IProcessorRepository>();
        var down = TrayPlatformHealth.Down(
            await sp.GetRequiredService<IConnectionCheckRecorder>().ListAsync(CancellationToken.None).ConfigureAwait(false),
            await connections.ListDownloadClientsAsync(CancellationToken.None).ConfigureAwait(false),
            await connections.ListIndexersAsync(CancellationToken.None).ConfigureAwait(false),
            await processors.ListProcessorConnectionsAsync(CancellationToken.None).ConfigureAwait(false),
            await sp.GetRequiredService<IHealthCheckRepository>().ListAsync(CancellationToken.None).ConfigureAwait(false),
            DateTimeOffset.UtcNow,
            HostStartedUtc);
        var settings = await sp.GetRequiredService<IPlatformSettingsRepository>().GetAsync(CancellationToken.None).ConfigureAwait(false);
        return new TrayServerSignals(down, !settings.AutoStartJobs);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        if (_publisher is not null)
        {
            _publisher.EventPublished -= OnPublished;
        }
    }
}

/// <summary>
/// Decides which of the things Deluno relies on do not answer, from the server's own health data: the latest real
/// check of each connection (what ConnectionChecked pushes), each connection's stored health, and the health problems
/// for a library folder, the metadata service or the disk. Download failures, hand-offs and the bell's rows are not read.
/// </summary>
public static class TrayPlatformHealth
{
    /// <summary>A stored connection health that means it does not answer.</summary>
    public static bool IsDownStatus(string? status)
        => status is not null && status.ToLowerInvariant() is "unreachable" or "unhealthy" or "failed" or "error";

    // After a restart the check taken before the host started is stale: Deluno's own broker (named "deluno") was still
    // starting. The watcher runs one fresh check straight after the start and that replaces it (#1202).
    public const string MetadataNotAnswering = "The metadata service isn't answering";

    public static string NotAnswering(string name) => $"{name} isn't answering";

    /// <summary>A Usenet client that answers but has no news server is a setup problem, not a client that is down (#1198).</summary>
    public static string NoUsenetProvider(string name) => $"{name} has no Usenet provider set up";

    private static bool HasNoProvider(string? message)
        => string.Equals(message?.Trim(), Deluno.Connections.Usenet.UsenetProviderText.NoProvider, StringComparison.Ordinal);

    /// <summary>
    /// Whether one dependency answers, from its latest real check (null when nothing has checked it) and its stored
    /// health with the time it was written. See <see cref="Down"/> for why a later non-check write cannot hide an outage.
    /// </summary>
    public static bool Judge(ConnectionCheckItem? check, string? status, DateTimeOffset? statusUtc)
    {
        if (check is null)
        {
            return !IsDownStatus(status);
        }

        if (statusUtc is not { } stored || stored <= check.CheckedUtc)
        {
            return check.Ok;
        }

        return check.Ok
            ? !IsDownStatus(status)
            : string.Equals(status?.Trim(), "healthy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One plain phrase for each thing that is down: download clients, then indexers, the processor, then the rest.</summary>
    public static IReadOnlyList<string> Down(
        IReadOnlyList<ConnectionCheckItem> checks,
        IReadOnlyList<DownloadClientItem> clients,
        IReadOnlyList<IndexerItem> indexers,
        IReadOnlyList<ProcessorConnectionItem> processors,
        IReadOnlyList<HealthCheckResultItem> health,
        DateTimeOffset now,
        DateTimeOffset? hostStartedUtc = null)
    {
        var latest = checks.ToDictionary(check => $"{check.Kind}:{check.Id}", StringComparer.OrdinalIgnoreCase);
        var down = new List<string>();

        // The latest real check wins; a connection nothing has checked yet is judged by its stored health.
        // A stored health written after a check wins only where it can be trusted: one that says "down" always, and one
        // that says "healthy" (an answer: an accepted hand-off, a pass that heard from it). Any other later write (a
        // hand-off that failed stores "degraded") is not an answer, so it never turns a dependency the latest check
        // found not answering back to answering (#1213). Only a successful check or answer does that.
        bool Answers(string kind, string id, string? status, DateTimeOffset? statusUtc)
            => Judge(latest.TryGetValue($"{kind}:{id}", out var check) ? check : null, status, statusUtc);

        // A Usenet client that signs in but has no news server yet says so in plain words, whether the latest real
        // check or the stored health carries the reason. It still counts as a problem, so the dot stays amber.
        string ClientProblem(DownloadClientItem item)
            => HasNoProvider(latest.TryGetValue($"{ConnectionKinds.DownloadClient}:{item.Id}", out var check) ? check.Message : item.LastHealthMessage)
                ? NoUsenetProvider(item.Name)
                : NotAnswering(item.Name);

        down.AddRange(clients.Where(item => item.IsEnabled && !Answers(ConnectionKinds.DownloadClient, item.Id, item.HealthStatus, item.LastHealthTestUtc)).Select(ClientProblem));
        down.AddRange(indexers.Where(item => item.IsEnabled && !Answers(ConnectionKinds.Indexer, item.Id, item.HealthStatus, item.LastHealthTestUtc)).Select(item => NotAnswering(item.Name)));
        down.AddRange(processors.Where(item => item.IsEnabled && !Answers(ConnectionKinds.Processor, item.Id, item.HealthStatus, item.LastHealthTestUtc)).Select(item => NotAnswering(item.Name)));

        var metadata = checks.FirstOrDefault(check => check.Kind == ConnectionKinds.Metadata);
        var starting = hostStartedUtc is { } started && metadata is not null && metadata.CheckedUtc < started;
        if (metadata is { Ok: false } && !starting)
        {
            // Deluno's own broker is named "deluno", which says nothing to a person: use the plain phrase for it.
            down.Add(string.Equals(metadata.Name, "deluno", StringComparison.OrdinalIgnoreCase) ? MetadataNotAnswering : NotAnswering(metadata.Name));
        }

        foreach (var problem in health.Where(item =>
                     string.Equals(item.Status, HealthCheckStatuses.Problem, StringComparison.OrdinalIgnoreCase) && !item.IsSnoozed(now)))
        {
            if (problem.Id.StartsWith("library:", StringComparison.Ordinal) && problem.Id.EndsWith(":root", StringComparison.Ordinal))
            {
                var library = problem.Name.EndsWith(" (library root)", StringComparison.Ordinal)
                    ? problem.Name[..^" (library root)".Length]
                    : problem.Name;
                down.Add($"The {library} library folder has a problem");
            }
            else if (problem.Id == "system:disk-space")
            {
                down.Add("Deluno's own disk is running low");
            }
            else if (problem.Id == "metadata:provider" && metadata is null)
            {
                down.Add(MetadataNotAnswering);
            }
        }

        return down;
    }
}
