using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Infrastructure.Observability;

/// <summary>
/// Where Deluno's own log goes on disk, and for how long it is kept.
/// </summary>
public sealed class DelunoFileLogOptions
{
    public const string SectionName = "Logging:File";

    public bool Enabled { get; set; } = true;

    /// <summary>Absolute or data-root-relative folder. Defaults to <c>{DataRoot}/logs</c>.</summary>
    public string? Directory { get; set; }

    /// <summary>Days a log file is kept after it was last written.</summary>
    public int RetainDays { get; set; } = 14;

    /// <summary>A day's file rolls to a numbered sibling once it reaches this size.</summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// The level a fresh start boots with, before any runtime change made
    /// through the Settings screen (#866) is read from its own persisted
    /// file. Matches Radarr's own default.
    /// </summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// Deluno's durable application log: one JSON object per line, one file per UTC
/// day, under the data root.
///
/// <para><b>Found on the physical rig.</b> The host used only the ASP.NET Core
/// default providers. Under a scheduled task, a Windows service or the tray, the
/// console is discarded and nothing reached a file or the Windows event log, so
/// a headless or packaged install kept no application log at all. Docker was
/// the only install whose log survived, because Docker keeps standard output.
/// "How did this file get here" then had nothing to answer from except
/// Activity.</para>
///
/// <para>Each line carries the timestamp, level, category, event, rendered
/// message, the structured properties the message was written with, the
/// enclosing scopes (the request trace id among them) and the exception. So a
/// single <c>TraceId</c> or dispatch id can be followed through the whole file.
/// Values under secret-looking names are masked, and secrets quoted inside
/// messages are masked in place.</para>
///
/// <para>Writing never blocks the caller: lines go to a bounded queue drained by
/// one background writer. If the queue is ever full the line is dropped and
/// counted, rather than stalling an import to wait for a disk.</para>
/// </summary>
[ProviderAlias("DelunoFile")]
public sealed class DelunoFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    public const string FilePrefix = "deluno-";
    public const string FileExtension = ".jsonl";

    private readonly DelunoFileLogOptions options;
    private readonly TimeProvider timeProvider;
    private readonly Channel<string> queue;
    private readonly Task writer;
    private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();
    private long dropped;

    private readonly IRuntimeLogLevelStore? runtimeLogLevelStore;
    private readonly DelunoLogBuffer? logBuffer;

    public DelunoFileLoggerProvider(
        IOptions<StoragePathOptions> storage,
        IOptions<DelunoFileLogOptions> options,
        TimeProvider timeProvider,
        IRuntimeLogLevelStore? runtimeLogLevelStore = null,
        DelunoLogBuffer? logBuffer = null)
    {
        this.logBuffer = logBuffer;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.runtimeLogLevelStore = runtimeLogLevelStore;
        LogDirectory = ResolveDirectory(storage.Value.DataRoot, this.options.Directory);
        queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
        writer = this.options.Enabled ? Task.Run(WriteLoopAsync) : Task.CompletedTask;
    }

    public string LogDirectory { get; }

    /// <summary>Lines refused because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    public ILogger CreateLogger(string categoryName) => new DelunoFileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => this.scopeProvider = scopeProvider;

    public void Dispose()
    {
        queue.Writer.TryComplete();
        try
        {
            writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Shutdown must not fail because the last lines could not be written.
        }
    }

    internal bool IsEnabled => options.Enabled;

    internal bool MeetsMinLevel(LogLevel logLevel)
        => runtimeLogLevelStore is null || logLevel >= runtimeLogLevelStore.Current;

    internal void Enqueue<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = Format(category, level, eventId, state, exception, formatter, out var message);
        if (logBuffer is not null && DelunoLogBuffer.IsNotable(level, category))
        {
            logBuffer.Add(level, category, message);
        }

        if (!queue.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref dropped);
        }
    }

    private string Format<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter,
        out string redactedMessage)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", timeProvider.GetUtcNow().UtcDateTime.ToString("O"));
            json.WriteString("level", level.ToString());
            json.WriteString("category", category);
            if (eventId.Id != 0)
            {
                json.WriteNumber("eventId", eventId.Id);
            }

            if (!string.IsNullOrEmpty(eventId.Name))
            {
                json.WriteString("eventName", eventId.Name);
            }

            // The rendered message contains every property value, so a secret
            // hidden in the properties would be printed here in plain words
            // ("with key {ApiKey}"). Mask those exact values in the sentence too.
            var message = SensitiveDataRedactor.RedactInline(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> stateProperties)
            {
                foreach (var secret in SecretValues(stateProperties))
                {
                    message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
                }
            }

            json.WriteString("message", message);
            redactedMessage = message;

            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                WriteProperties(json, "properties", properties, skipTemplate: false);
            }

            var scopes = new List<object?>();
            scopeProvider.ForEachScope(static (scope, list) => list.Add(scope), scopes);
            if (scopes.Count > 0)
            {
                json.WriteStartObject("scope");
                var index = 0;
                foreach (var scope in scopes)
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopeProperties)
                    {
                        WritePairs(json, scopeProperties, skipTemplate: true);
                    }
                    else if (scope is not null)
                    {
                        json.WriteString($"scope{index}", SensitiveDataRedactor.RedactInline(scope.ToString()));
                    }

                    index++;
                }

                json.WriteEndObject();
            }

            if (exception is not null)
            {
                json.WriteString("exception", SensitiveDataRedactor.RedactInline(exception.ToString()));
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteProperties(Utf8JsonWriter json, string name, IEnumerable<KeyValuePair<string, object?>> pairs, bool skipTemplate)
    {
        json.WriteStartObject(name);
        WritePairs(json, pairs, skipTemplate);
        json.WriteEndObject();
    }

    /// <summary>
    /// Names whose values must never be written: secret-looking names, and any
    /// placeholder the template puts straight after one. A secret usually
    /// reaches a log inside a URL (<c>...?apikey={Key}</c>), so its property is
    /// named for its place rather than for being a secret.
    /// </summary>
    private static HashSet<string> SecretNames(IReadOnlyCollection<KeyValuePair<string, object?>> pairs)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (pairs.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value is string template)
        {
            foreach (System.Text.RegularExpressions.Match match in SecretPlaceholder.Matches(template))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        foreach (var pair in pairs)
        {
            if (pair.Key != "{OriginalFormat}" && SensitiveDataRedactor.IsSensitiveName(pair.Key))
            {
                names.Add(pair.Key);
            }
        }

        return names;
    }

    private static IEnumerable<string> SecretValues(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var materialised = pairs as IReadOnlyCollection<KeyValuePair<string, object?>> ?? pairs.ToList();
        var names = SecretNames(materialised);
        return materialised
            .Where(pair => names.Contains(pair.Key))
            .Select(pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture))
            // Very short values would mask ordinary words across the sentence.
            .Where(value => value is { Length: >= 4 })
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length);
    }

    private static void WritePairs(Utf8JsonWriter json, IEnumerable<KeyValuePair<string, object?>> pairs, bool skipTemplate)
    {
        var materialised = pairs as IReadOnlyCollection<KeyValuePair<string, object?>> ?? pairs.ToList();
        var secretPlaceholders = SecretNames(materialised);

        foreach (var (key, value) in materialised)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var propertyName = key == "{OriginalFormat}" ? "template" : key;
            if (skipTemplate && propertyName == "template")
            {
                continue;
            }

            if (SensitiveDataRedactor.IsSensitiveName(propertyName) || secretPlaceholders.Contains(propertyName))
            {
                json.WriteString(propertyName, "[redacted]");
                continue;
            }

            switch (value)
            {
                case null:
                    json.WriteNull(propertyName);
                    break;
                case bool flag:
                    json.WriteBoolean(propertyName, flag);
                    break;
                case int or long or short or byte or uint or ulong or ushort or sbyte:
                    json.WriteNumber(propertyName, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case double or float or decimal:
                    json.WriteNumber(propertyName, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case DateTimeOffset moment:
                    json.WriteString(propertyName, moment.ToUniversalTime().ToString("O"));
                    break;
                case DateTime moment:
                    json.WriteString(propertyName, moment.ToUniversalTime().ToString("O"));
                    break;
                default:
                    json.WriteString(propertyName, propertyName == "template"
                        ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                        : SensitiveDataRedactor.RedactInline(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                    break;
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? stream = null;
        string? currentPath = null;
        // Counted as written rather than read from the stream: the writer
        // buffers, so the file's length lags a burst and a burst would overrun
        // the size cap in one file.
        long currentBytes = 0;
        var newLineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
        try
        {
            System.IO.Directory.CreateDirectory(LogDirectory);
            PruneExpired();

            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var line))
                {
                    var path = PathFor(timeProvider.GetUtcNow(), currentBytes, currentPath);
                    if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (stream is not null)
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);
                            PruneExpired();
                        }

                        stream = Open(path);
                        currentPath = path;
                        currentBytes = stream.BaseStream.Length;
                    }

                    await stream!.WriteLineAsync(line).ConfigureAwait(false);
                    currentBytes += Encoding.UTF8.GetByteCount(line) + newLineBytes;
                }

                if (stream is not null)
                {
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must never take the application down
            // with it. The console providers still have the lines.
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string PathFor(DateTimeOffset now, long currentLength, string? currentPath)
    {
        var day = now.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var basePath = Path.Combine(LogDirectory, $"{FilePrefix}{day}{FileExtension}");
        var sameDay = currentPath is not null &&
            Path.GetFileName(currentPath).StartsWith($"{FilePrefix}{day}", StringComparison.OrdinalIgnoreCase);

        if (sameDay && currentLength < options.MaxFileBytes)
        {
            return currentPath!;
        }

        // First line of the day, or the day's file is full: the next free name.
        for (var part = 0; ; part++)
        {
            var candidate = part == 0 ? basePath : Path.Combine(LogDirectory, $"{FilePrefix}{day}.{part}{FileExtension}");
            if (sameDay && string.Equals(candidate, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(candidate) || new FileInfo(candidate).Length < options.MaxFileBytes)
            {
                return candidate;
            }
        }
    }

    private static StreamWriter Open(string path)
    {
        // Shared read, so the file can be tailed or copied while Deluno runs.
        var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Removes Deluno's own expired log files and nothing else: only names this
    /// provider writes, only in its own folder.
    /// </summary>
    private void PruneExpired()
    {
        if (options.RetainDays <= 0)
        {
            return;
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-options.RetainDays);
        foreach (var file in new DirectoryInfo(LogDirectory).EnumerateFiles($"{FilePrefix}*{FileExtension}", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    file.Delete();
                }
            }
            catch (IOException)
            {
                // Held open elsewhere; the next roll tries again.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SecretPlaceholder = new(
        @"\b(?:api[_-]?key|apikey|password|passwd|access[_-]?token|token|secret)\s*[=:]\s*\{(?<name>[^}:,]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Where log files live for a given data root and configured directory,
    /// without needing a live <see cref="DelunoFileLoggerProvider"/> instance
    /// -- the Log Files list (#867) and the file prefix/extension constants
    /// above are enough to enumerate them from an endpoint.
    /// </summary>
    public static string ResolveDirectory(string dataRoot, string? configured)
        => string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(dataRoot, "logs")
            : Path.GetFullPath(configured, dataRoot);

    private sealed class DelunoFileLogger(DelunoFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => provider.scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled && logLevel != LogLevel.None && provider.MeetsMinLevel(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Enqueue(category, logLevel, eventId, state, exception, formatter);
        }
    }
}
