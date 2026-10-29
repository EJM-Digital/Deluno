namespace Deluno.Security.Recovery;

/// <summary>Tells the rest of Deluno that a password was reset, so it can write Activity and send a notification (#1392).</summary>
public interface IAccountResetReporter
{
    Task ReportResetAsync(CancellationToken cancellationToken);
}

public sealed class NoOpAccountResetReporter : IAccountResetReporter
{
    public Task ReportResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Five wrong tries in a row lock recovery for ten minutes, for everyone; there is only one account to protect.
/// </summary>
public class RecoveryAttempts(TimeProvider timeProvider)
{
    public const int AllowedFailures = 5;
    public static readonly TimeSpan LockLifetime = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private int failures;
    private DateTimeOffset lockedUntilUtc;

    public bool IsLocked()
    {
        lock (gate)
        {
            return timeProvider.GetUtcNow() < lockedUntilUtc;
        }
    }

    public void Failed()
    {
        lock (gate)
        {
            failures++;
            if (failures >= AllowedFailures)
            {
                failures = 0;
                lockedUntilUtc = timeProvider.GetUtcNow().Add(LockLifetime);
            }
        }
    }

    public void Succeeded()
    {
        lock (gate)
        {
            failures = 0;
        }
    }
}

/// <summary>
/// The same limit for the current-password check (#1433), counted on its own so a locked password check does not
/// lock recovery, and the other way round.
/// </summary>
public sealed class CurrentPasswordAttempts(TimeProvider timeProvider) : RecoveryAttempts(timeProvider);
