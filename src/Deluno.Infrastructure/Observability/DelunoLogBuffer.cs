using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Deluno.Infrastructure.Observability;

/// <summary>One line of Deluno's own log as the System screen shows it (#1098).</summary>
public sealed record LogEntryView(string Id, DateTimeOffset TimeUtc, string Level, string Message, string? Source);

/// <summary>
/// Today's notable log lines, kept in memory so the System screen's Log card can show them
/// without reading a file: every warning and error, plus the information lines about the
/// things a person wants to know happened (a backup, an update, a database being compacted
/// or checked, start-up). Everything shown has already been through the file log's secret
/// redaction. Old entries are dropped when the buffer is full and when the day ends.
/// </summary>
public sealed class DelunoLogBuffer
{
    public const int Capacity = 500;

    // Information lines are only worth keeping when they are about one of these things.
    private static readonly string[] NotableCategories =
        ["Backup", "Migrat", "Startup", "Update", "Integrity", "Compact", "Restore", "Retention", "Upgrade"];

    private readonly object gate = new();
    private readonly List<LogEntryView> entries = new();
    private readonly TimeProvider timeProvider;
    private long counter;
    private bool seeded;

    public DelunoLogBuffer(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after an entry is kept, on the logging thread; handlers must not block.</summary>
    public event Action<LogEntryView>? Added;

    /// <summary>Whether a line of this level from this category is kept.</summary>
    public static bool IsNotable(LogLevel level, string category)
        => level >= LogLevel.Warning
           || (level == LogLevel.Information && NotableCategories.Any(word => category.Contains(word, StringComparison.OrdinalIgnoreCase)));

    public static string LevelName(LogLevel level)
        => level >= LogLevel.Error ? "error" : level == LogLevel.Warning ? "warning" : "info";

    public static string SourceName(string category)
    {
        var name = category[(category.LastIndexOf('.') + 1)..];
        return name.EndsWith("HostedService", StringComparison.Ordinal) ? name[..^"HostedService".Length]
            : name.EndsWith("Service", StringComparison.Ordinal) && name.Length > 7 ? name[..^"Service".Length]
            : name;
    }

    /// <summary>The first line of an already-redacted message, trimmed to a length one row can hold.</summary>
    public static string OneLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length <= 400 ? line : line[..397] + "...";
    }

    public void Add(LogLevel level, string category, string redactedMessage)
    {
        if (!IsNotable(level, category) || string.IsNullOrWhiteSpace(redactedMessage))
        {
            return;
        }

        var entry = new LogEntryView(
            $"live-{Interlocked.Increment(ref counter)}",
            timeProvider.GetUtcNow(),
            LevelName(level),
            OneLine(redactedMessage),
            SourceName(category));
        lock (gate)
        {
            entries.Add(entry);
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(0, entries.Count - Capacity);
            }
        }

        Added?.Invoke(entry);
    }

    /// <summary>Newest first. <paramref name="level"/> is all, error or warning.</summary>
    public IReadOnlyList<LogEntryView> Recent(string? level, int take)
    {
        var midnight = new DateTimeOffset(timeProvider.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        lock (gate)
        {
            return entries
                .Where(entry => entry.TimeUtc >= midnight)
                .Where(entry => level is null or "all" || entry.Level == level)
                .OrderByDescending(entry => entry.TimeUtc)
                .Take(Math.Clamp(take, 1, Capacity))
                .ToArray();
        }
    }

    /// <summary>
    /// After a restart the buffer is empty, so the first read fills it once from the tail of
    /// today's log files: the same lines, already redacted when they were written.
    /// </summary>
    public void SeedFromFiles(string directory)
    {
        lock (gate)
        {
            if (seeded)
            {
                return;
            }

            seeded = true;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var day = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var found = new List<LogEntryView>();
            foreach (var file in new DirectoryInfo(directory)
                         .EnumerateFiles($"{DelunoFileLoggerProvider.FilePrefix}{day}*{DelunoFileLoggerProvider.FileExtension}")
                         .OrderBy(file => file.Name, StringComparer.Ordinal))
            {
                foreach (var line in TailLines(file.FullName, 2 * 1024 * 1024))
                {
                    if (TryRead(line, out var entry))
                    {
                        found.Add(entry);
                    }
                }
            }

            lock (gate)
            {
                // Lines logged since start are already in (and are also in the file): add the file's
                // lines that are not.
                foreach (var entry in found)
                {
                    if (!entries.Any(have => have.Message == entry.Message && Math.Abs((have.TimeUtc - entry.TimeUtc).TotalSeconds) < 2))
                    {
                        entries.Add(entry);
                    }
                }

                if (entries.Count > Capacity)
                {
                    var kept = entries.OrderBy(entry => entry.TimeUtc).TakeLast(Capacity).ToList();
                    entries.Clear();
                    entries.AddRange(kept);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be read leaves the card with what was logged since start.
        }
    }

    private static List<string> TailLines(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipFirst = stream.Length > maxBytes;
        if (skipFirst)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        if (skipFirst)
        {
            reader.ReadLine();
        }

        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static bool TryRead(string line, out LogEntryView entry)
    {
        entry = null!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!Enum.TryParse<LogLevel>(root.GetProperty("level").GetString(), out var level)
                || !DateTimeOffset.TryParse(root.GetProperty("timestamp").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                return false;
            }

            var category = root.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (!IsNotable(level, category) || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            entry = new LogEntryView($"file-{time.UtcTicks}", time.ToUniversalTime(), LevelName(level), OneLine(message), SourceName(category));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
