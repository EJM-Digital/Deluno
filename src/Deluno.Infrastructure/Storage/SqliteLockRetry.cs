using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// How long start-up keeps trying when a database is locked by something else (#1080).
/// </summary>
public sealed class MigrationLockRetryOptions
{
    /// <summary>The longest Deluno keeps trying before it gives up and start-up fails.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The pause before the first retry; it doubles up to <see cref="MaxPause"/>.</summary>
    public TimeSpan FirstPause { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxPause { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Retries a start-up database step while another process holds the database
/// locked (a backup tool, a slow disk, a previous Deluno still closing),
/// instead of failing start-up on the first busy answer (#1080). Any other
/// error, and a lock that outlasts <see cref="MigrationLockRetryOptions.MaxWait"/>,
/// is thrown as it is, so start-up still fails on a database it could not prepare.
/// </summary>
public static class SqliteLockRetry
{
    public static bool IsLocked(Exception exception)
        => exception is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xFF) is 5 or 6;

    public static async Task RunAsync(
        Func<Task> step,
        string what,
        MigrationLockRetryOptions options,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var pause = options.FirstPause;
        while (true)
        {
            try
            {
                await step();
                return;
            }
            catch (SqliteException exception) when (IsLocked(exception) && clock.Elapsed + pause < options.MaxWait)
            {
                logger?.LogWarning(
                    "{What} is waiting: the database is locked by something else. Deluno will try again in {Pause} and gives up after {Limit}.",
                    what,
                    pause,
                    options.MaxWait);
                await Task.Delay(pause, cancellationToken);
                pause = TimeSpan.FromTicks(Math.Min(options.MaxPause.Ticks, pause.Ticks * 2));
            }
        }
    }
}
