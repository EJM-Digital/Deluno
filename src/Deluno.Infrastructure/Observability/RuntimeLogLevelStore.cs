using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Infrastructure.Observability;

/// <summary>
/// The minimum level Deluno's own log file writes at, changeable while
/// Deluno is running (#866). appsettings.json's <c>Logging:LogLevel</c>
/// still sets the level a fresh start boots with; a change made here on a
/// running instance is what takes effect immediately, and is what a
/// restart then boots with again, the same way appsettings.json does today.
/// </summary>
public interface IRuntimeLogLevelStore
{
    LogLevel Current { get; }

    Task SetAsync(LogLevel level, CancellationToken cancellationToken);
}

/// <summary>
/// Persisted as a small JSON file next to the data root, the same shape
/// DefaultUpdateOrchestrator already uses for its own preferences file.
/// </summary>
public sealed class FileRuntimeLogLevelStore : IRuntimeLogLevelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private LogLevel current;

    public FileRuntimeLogLevelStore(IOptions<StoragePathOptions> storage, IOptions<DelunoFileLogOptions> fileLogOptions)
    {
        path = Path.Combine(Path.GetFullPath(storage.Value.DataRoot), "log-level.json");
        current = ReadPersistedLevel() ?? fileLogOptions.Value.MinLevel;
    }

    public LogLevel Current => current;

    public async Task SetAsync(LogLevel level, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            current = level;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, new PersistedLevel(level.ToString()), JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private LogLevel? ReadPersistedLevel()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedLevel>(json, JsonOptions);
            return persisted is not null && Enum.TryParse<LogLevel>(persisted.Level, ignoreCase: true, out var level)
                ? level
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PersistedLevel(string Level);
}
