using System.Globalization;

namespace Deluno.Platform.Contracts;

/// <summary>
/// The one rule for a processor's <c>Retry-After</c> (#1266). Weir 1.0.0-rc.18 answers a write that waited the whole busy timeout with
/// <c>503</c> and a <c>Retry-After</c> in seconds (Weir #964); HTTP also allows a date, and a proxy in front of a processor may send either on a
/// 429 or 503. Every call Deluno makes to a processor (the hand-off, the status poll, the cancel, the capability read, the outcome report)
/// reads it here and waits the same way:
///
/// <list type="bullet">
/// <item>seconds (<c>30</c>) and an HTTP date (<c>Wed, 21 Oct 2026 07:28:00 GMT</c>) both work;</item>
/// <item>a header that is missing, empty, not a number or a date, zero, negative, or a date already past is "not given": Deluno keeps the schedule
/// it had before this rule, so a processor that sends rubbish is asked as often as ever;</item>
/// <item>a long wait is cut to <see cref="Longest"/>, so a processor cannot silence Deluno for a day;</item>
/// <item>the wait is the time given plus up to <see cref="JitterFraction"/> of it, so a crowd of callers does not return together.</item>
/// </list>
/// </summary>
public static class ProcessorRetryAfter
{
    public static readonly TimeSpan Longest = TimeSpan.FromHours(1);

    /// <summary>The most that is added to the time given, as a fraction of it.</summary>
    public const double JitterFraction = 0.1;

    /// <summary>The time the processor asked for, or null when it gave none that can be read.</summary>
    public static TimeSpan? Read(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        TimeSpan? asked = null;
        var typed = response.Headers.RetryAfter;
        if (typed?.Delta is { } delta)
        {
            asked = delta;
        }
        else if (typed?.Date is { } date)
        {
            asked = date - now;
        }
        else if (response.Headers.TryGetValues("Retry-After", out var raw))
        {
            // The typed reader refuses a value it cannot parse; a number with spaces or a date in another accepted format is read here.
            asked = Parse(raw.FirstOrDefault(), now);
        }

        if (asked is not { } wait || wait <= TimeSpan.Zero)
        {
            return null;
        }

        return wait > Longest ? Longest : wait;
    }

    /// <summary>The time in a header value, or null.</summary>
    public static TimeSpan? Parse(string? value, DateTimeOffset now)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, Longest.TotalSeconds));
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date - now : null;
    }

    /// <summary>
    /// How long to wait before asking again: the processor's time plus jitter when it gave one, otherwise <paramref name="scheduled"/> (the schedule
    /// the caller had). <paramref name="jitter"/> is a number from 0 to 1.
    /// </summary>
    public static TimeSpan Wait(TimeSpan scheduled, TimeSpan? retryAfter, double jitter)
        => retryAfter is { } asked ? asked + TimeSpan.FromTicks((long)(asked.Ticks * JitterFraction * Math.Clamp(jitter, 0, 1))) : scheduled;
}
