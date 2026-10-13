using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// Publishes the point at which every Deluno database has finished its schema
/// migrations. The web server can begin accepting requests before hosted
/// services have completed startup, so database reachability alone is not a
/// safe readiness signal.
/// </summary>
public interface IDelunoStartupGate
{
    /// <summary>Whether all registered database migrations have completed.</summary>
    bool IsReady { get; }

    /// <summary>Databases whose schema initializers have not completed.</summary>
    IReadOnlyList<string> PendingDatabases { get; }

    /// <summary>Schema initializer failures recorded during startup.</summary>
    IReadOnlyDictionary<string, string> FailedDatabases { get; }

    /// <summary>Waits until all schema initializers have completed.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Marks one database's migration set as complete.</summary>
    void MarkReady(string databaseName);

    /// <summary>Records a migration failure while allowing readiness to explain it.</summary>
    void MarkFailed(string databaseName, Exception exception);
}

public sealed class DelunoStartupGate(IStartupProgress? progress = null) : IDelunoStartupGate
{
    private static readonly IReadOnlySet<string> ExpectedDatabases =
        DelunoStorageLayout.Databases
            .Select(database => database.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> readyDatabases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> failedDatabases = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource allReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => allReady.Task.IsCompletedSuccessfully;

    public IReadOnlyList<string> PendingDatabases
        => ExpectedDatabases
            .Where(databaseName => !readyDatabases.ContainsKey(databaseName))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyDictionary<string, string> FailedDatabases
        => new Dictionary<string, string>(failedDatabases, StringComparer.OrdinalIgnoreCase);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return;
        }

        await allReady.Task.WaitAsync(timeout, cancellationToken);
    }

    public void MarkReady(string databaseName)
    {
        if (!ExpectedDatabases.Contains(databaseName))
        {
            throw new ArgumentException(
                $"'{databaseName}' is not a registered Deluno database.",
                nameof(databaseName));
        }

        failedDatabases.TryRemove(databaseName, out _);
        readyDatabases[databaseName] = 0;
        if (readyDatabases.Count == ExpectedDatabases.Count && failedDatabases.IsEmpty && allReady.TrySetResult())
        {
            // The databases are done; what is left is the remaining services and the web server (#1256).
            progress?.Enter(StartupPhase.WebHost, "the databases are ready");
        }
    }

    public void MarkFailed(string databaseName, Exception exception)
    {
        if (!ExpectedDatabases.Contains(databaseName))
        {
            throw new ArgumentException(
                $"'{databaseName}' is not a registered Deluno database.",
                nameof(databaseName));
        }

        readyDatabases.TryRemove(databaseName, out _);
        failedDatabases[databaseName] = exception.Message;
    }
}

public static class DelunoStartupGateExtensions
{
    /// <summary>
    /// Waits for every database to be prepared and never throws for a timeout
    /// (#1080). Background loops that need the databases used to call
    /// <see cref="IDelunoStartupGate.WaitAsync"/> once with a two-minute limit;
    /// on a slow start (a busy disk, a locked database being waited for) the
    /// <see cref="TimeoutException"/> ended the loop for good. This keeps waiting
    /// and says so in the log every <paramref name="slice"/>.
    /// </summary>
    /// <returns>True once the databases are ready; false only when <paramref name="stoppingToken"/> is cancelled.</returns>
    public static async Task<bool> WaitUntilReadyAsync(
        this IDelunoStartupGate gate,
        ILogger logger,
        string service,
        CancellationToken stoppingToken,
        TimeSpan? slice = null)
    {
        var wait = slice ?? TimeSpan.FromMinutes(2);
        while (true)
        {
            try
            {
                await gate.WaitAsync(wait, stoppingToken);
                return true;
            }
            catch (TimeoutException)
            {
                logger.LogWarning(
                    "{Service} is still waiting for Deluno's databases to be ready ({Pending}); it keeps waiting.",
                    service,
                    string.Join(", ", gate.PendingDatabases));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
