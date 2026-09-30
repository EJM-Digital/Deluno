using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// The one place a task or activity failure becomes plain words (#1095), and the
/// one place a retry time becomes "in about 5 minutes". The raw exception text is
/// never shown on a screen; callers keep it in the row's details and in the log.
/// </summary>
public static class PlainFailure
{
    public const string UnreadableKey = "A saved key can't be read on this computer. Enter it again on the connection's page.";

    private const string TooSlow = "The service took too long to answer.";
    private const string Refused = "The service refused the connection. Check that it is running.";
    private const string NoAddress = "Deluno couldn't find the service's address. Check it is spelled right.";
    private const string DiskFull = "The disk is full. Free up some space and Deluno will carry on.";
    private const string NoAccess = "Deluno isn't allowed to use a file or folder it needs. Check the folder's permissions.";

    // IOException HResults for "no space left" (Windows ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL, Linux ENOSPC).
    private const int WindowsDiskFull = unchecked((int)0x80070070);
    private const int WindowsHandleDiskFull = unchecked((int)0x80070027);
    private const int LinuxNoSpace = 28;

    /// <summary>
    /// What went wrong, in everyday words. <paramref name="taskLabel"/> is the task
    /// in lower case mid-sentence ("searching the film library"); it is used only
    /// when the failure is not one of the common kinds.
    /// </summary>
    public static string Describe(Exception exception, string? taskLabel = null)
    {
        var root = Unwrap(exception);
        switch (root)
        {
            case CryptographicException:
                return UnreadableKey;
            case HttpRequestException http:
                if (http.StatusCode is { } status)
                {
                    return ForStatus((int)status) ?? Generic(taskLabel);
                }

                return http.InnerException is SocketException socket
                    ? ForSocket(socket)
                    : "Deluno couldn't connect to the service. Check that it is running and the address is right.";
            case SocketException socketException:
                return ForSocket(socketException);
            case TimeoutException:
            case TaskCanceledException:
                return TooSlow;
            case UnauthorizedAccessException:
                return NoAccess;
            case FileNotFoundException:
            case DirectoryNotFoundException:
                return "A file or folder Deluno needs couldn't be found.";
            case IOException io when io.HResult is WindowsDiskFull or WindowsHandleDiskFull or LinuxNoSpace:
                return DiskFull;
        }

        return Generic(taskLabel);
    }

    /// <summary>"Deluno will try again in about 5 minutes."</summary>
    public static string RetrySentence(TimeSpan delay) => $"Deluno will try again {InWords(delay)}.";

    /// <summary>"in about 5 minutes", "in about an hour", "shortly".</summary>
    public static string InWords(TimeSpan delay)
    {
        if (delay <= TimeSpan.FromSeconds(45))
        {
            return "shortly";
        }

        if (delay < TimeSpan.FromMinutes(1.5))
        {
            return "in about a minute";
        }

        if (delay < TimeSpan.FromMinutes(55))
        {
            return $"in about {Math.Max(2, (int)Math.Round(delay.TotalMinutes))} minutes";
        }

        if (delay < TimeSpan.FromMinutes(90))
        {
            return "in about an hour";
        }

        if (delay < TimeSpan.FromHours(22))
        {
            return $"in about {(int)Math.Round(delay.TotalHours)} hours";
        }

        if (delay < TimeSpan.FromHours(36))
        {
            return "in about a day";
        }

        return $"in about {(int)Math.Round(delay.TotalDays)} days";
    }

    private const string IsoPattern = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}[0-9:.]*(?:Z|[+-]\d{2}:\d{2})?";

    private static readonly Regex RetryAfterIso = new(
        @"(?<who>Deluno\s+)?will retry\s+after\s+(?<ts>" + IsoPattern + @")\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ScheduledForIso = new(
        @"scheduled for\s+(?<ts>" + IsoPattern + @")\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AnyIso = new(IsoPattern, RegexOptions.CultureInvariant);

    // Raw exception sentences older rows still carry, worded the way Describe would word them.
    private static readonly (string Raw, string Plain)[] KnownRawSentences =
    [
        ("Key not valid for use in specified state.", UnreadableKey),
        ("Key not valid for use in specified state", UnreadableKey),
        ("The operation was canceled.", TooSlow),
        ("No connection could be made because the target machine actively refused it.", Refused),
        ("No connection could be made because the target machine actively refused it", Refused),
        ("No such host is known.", NoAddress),
        ("No such host is known", NoAddress),
        ("Name or service not known", NoAddress),
        ("There is not enough space on the disk.", DiskFull),
        ("No space left on device", DiskFull),
    ];

    /// <summary>
    /// Reads an old row in plain words: an ISO retry time becomes a time relative
    /// to the row's own moment, and a known raw exception sentence becomes its
    /// plain version. Rows already in plain words pass through unchanged.
    /// </summary>
    public static string Rewrite(string? message, DateTimeOffset eventUtc)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        var text = message;
        foreach (var (raw, plain) in KnownRawSentences)
        {
            if (text.Contains(raw, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Replace(raw, plain, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!AnyIso.IsMatch(text))
        {
            return text;
        }

        text = RetryAfterIso.Replace(text, match => Delay(
            match.Groups["ts"].Value,
            eventUtc,
            delay => match.Groups["who"].Success ? RetrySentence(delay) : $"will try again {InWords(delay)}."));
        text = ScheduledForIso.Replace(text, match => Delay(match.Groups["ts"].Value, eventUtc, delay => $"scheduled {InWords(delay)}."));
        return AnyIso.Replace(text, match =>
            DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? InWords(at - eventUtc)
                : "later");
    }

    private static string Delay(string iso, DateTimeOffset eventUtc, Func<TimeSpan, string> words)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return words(TimeSpan.Zero);
        }

        var delay = at - eventUtc;
        return words(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
    }

    private static string? ForStatus(int status) => status switch
    {
        401 or 403 => "The service refused Deluno's saved login or key. Check it on the connection's page.",
        404 => "The service said that address doesn't exist. Check the connection's address.",
        429 => "The service said Deluno is asking too often and told it to slow down.",
        >= 500 and <= 599 => "The service had a problem on its side.",
        _ => null
    };

    private static string ForSocket(SocketException socket) => socket.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => Refused,
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => NoAddress,
        SocketError.TimedOut => TooSlow,
        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown => "Deluno couldn't reach the service over the network.",
        _ => "Deluno couldn't connect to the service. Check that it is running and the address is right."
    };

    private static string Generic(string? taskLabel) =>
        string.IsNullOrWhiteSpace(taskLabel)
            ? "Something went wrong."
            : $"Something went wrong while {taskLabel.Trim().TrimEnd('.')}.";

    // A crypto failure buried under a wrapper is the real reason; otherwise the exception itself.
    private static Exception Unwrap(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is CryptographicException)
            {
                return inner;
            }
        }

        var current = exception;
        while (current is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            current = aggregate.InnerExceptions[0];
        }

        return current;
    }
}
