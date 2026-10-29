using System.Security.Cryptography;
using Deluno.Security.Contracts;
using Deluno.Security.Data;

namespace Deluno.Security.Recovery;

/// <summary>Whether this host runs in a container, and whether a caller is the person at that container's own console (#1392).</summary>
public interface IRecoveryHostInfo
{
    bool InContainer { get; }

    bool IsCallerInsideTheHost(Microsoft.AspNetCore.Http.HttpContext context);
}

public sealed class RecoveryHostInfo : IRecoveryHostInfo
{
    public bool InContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A call made from inside the container itself: the socket peer is the loopback address and no proxy added a
    /// forwarding header. A browser, even through a proxy on the same machine, never qualifies.
    /// </summary>
    public bool IsCallerInsideTheHost(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var peer = Hardening.LocalNetworkAuthConstants.SocketPeer(context);
        if (peer is null || !System.Net.IPAddress.IsLoopback(peer))
        {
            return false;
        }

        return !context.Request.Headers.ContainsKey("X-Forwarded-For")
            && !context.Request.Headers.ContainsKey("Forwarded")
            && !context.Request.Headers.ContainsKey("X-Real-IP");
    }
}

/// <summary>Tells the rest of Deluno that a password was reset, so it can write Activity and send a notification (#1392).</summary>
public interface IAccountResetReporter
{
    Task ReportResetAsync(RecoveryMethod method, CancellationToken cancellationToken);
}

public enum RecoveryMethod
{
    RecoveryCode,
    OneTimePin
}

public sealed class NoOpAccountResetReporter : IAccountResetReporter
{
    public Task ReportResetAsync(RecoveryMethod method, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The one-time PIN: made by a command run inside the container, kept only as a hash, good for fifteen minutes and one
/// use. A new PIN replaces the one before it.
/// </summary>
public sealed class RecoveryPins(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly object gate = new();
    private string? hash;
    private DateTimeOffset expiresUtc;

    public RecoveryPinMade Make()
    {
        var pin = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        lock (gate)
        {
            hash = SqliteSecurityRepository.HashPassword(pin);
            expiresUtc = timeProvider.GetUtcNow().Add(Lifetime);
            return new RecoveryPinMade(pin, expiresUtc);
        }
    }

    public bool Matches(string? typed)
    {
        lock (gate)
        {
            return IsLive() && SqliteSecurityRepository.VerifyPassword(Digits(typed), hash!);
        }
    }

    /// <summary>True once, for a live PIN that matches; the PIN is gone afterwards.</summary>
    public bool TryUse(string? typed)
    {
        lock (gate)
        {
            if (!IsLive() || !SqliteSecurityRepository.VerifyPassword(Digits(typed), hash!))
            {
                return false;
            }

            hash = null;
            return true;
        }
    }

    private bool IsLive() => hash is not null && timeProvider.GetUtcNow() < expiresUtc;

    private static string Digits(string? typed) => new((typed ?? string.Empty).Where(char.IsDigit).ToArray());
}

/// <summary>
/// Five wrong tries in a row (a recovery code or a PIN) lock recovery for ten minutes, for everyone; there is only
/// one account to protect.
/// </summary>
public sealed class RecoveryAttempts(TimeProvider timeProvider)
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
