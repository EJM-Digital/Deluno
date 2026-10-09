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

    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(500);

    private readonly IServiceProvider _services;
    private readonly SignalRRealtimeEventPublisher? _publisher;
    private readonly Action<TrayServerSignals> _changed;
    private readonly object _lock = new();
    private bool _scheduled;
    private bool _disposed;
    private bool _everRead;

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
            DateTimeOffset.UtcNow);
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

    public static string NotAnswering(string name) => $"{name} isn't answering";

    /// <summary>A Usenet client that answers but has no news server is a setup problem, not a client that is down (#1198).</summary>
    public static string NoUsenetProvider(string name) => $"{name} has no Usenet provider set up";

    private static bool HasNoProvider(string? message)
        => string.Equals(message?.Trim(), Deluno.Connections.Usenet.UsenetProviderText.NoProvider, StringComparison.Ordinal);

    /// <summary>One plain phrase for each thing that is down: download clients, then indexers, the processor, then the rest.</summary>
    public static IReadOnlyList<string> Down(
        IReadOnlyList<ConnectionCheckItem> checks,
        IReadOnlyList<DownloadClientItem> clients,
        IReadOnlyList<IndexerItem> indexers,
        IReadOnlyList<ProcessorConnectionItem> processors,
        IReadOnlyList<HealthCheckResultItem> health,
        DateTimeOffset now)
    {
        var latest = checks.ToDictionary(check => $"{check.Kind}:{check.Id}", StringComparer.OrdinalIgnoreCase);
        var down = new List<string>();

        // The latest real check wins; a connection nothing has checked yet is judged by its stored health.
        bool Answers(string kind, string id, string? status)
            => latest.TryGetValue($"{kind}:{id}", out var check) ? check.Ok : !IsDownStatus(status);

        // A Usenet client that signs in but has no news server yet says so in plain words, whether the latest real
        // check or the stored health carries the reason. It still counts as a problem, so the dot stays amber.
        string ClientProblem(DownloadClientItem item)
            => HasNoProvider(latest.TryGetValue($"{ConnectionKinds.DownloadClient}:{item.Id}", out var check) ? check.Message : item.LastHealthMessage)
                ? NoUsenetProvider(item.Name)
                : NotAnswering(item.Name);

        down.AddRange(clients.Where(item => item.IsEnabled && !Answers(ConnectionKinds.DownloadClient, item.Id, item.HealthStatus)).Select(ClientProblem));
        down.AddRange(indexers.Where(item => item.IsEnabled && !Answers(ConnectionKinds.Indexer, item.Id, item.HealthStatus)).Select(item => NotAnswering(item.Name)));
        down.AddRange(processors.Where(item => item.IsEnabled && !Answers(ConnectionKinds.Processor, item.Id, item.HealthStatus)).Select(item => NotAnswering(item.Name)));

        var metadata = checks.FirstOrDefault(check => check.Kind == ConnectionKinds.Metadata);
        if (metadata is { Ok: false })
        {
            down.Add(NotAnswering(metadata.Name));
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
                down.Add("The metadata service isn't answering");
            }
        }

        return down;
    }
}
