using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Jobs.Data;

/// <summary>
/// How many Activity timeline rows the System screen fetches and shows
/// (#871). Radarr's Events page has a page-size control (default 50);
/// Deluno's own timeline was hardcoded to a fixed 500-row cap with no way
/// to change it.
/// </summary>
public interface IActivityDisplaySettingsStore
{
    int PageSize { get; }

    Task SetPageSizeAsync(int pageSize, CancellationToken cancellationToken);
}

/// <summary>Persisted as a small JSON file next to the data root, the same shape the runtime log level uses.</summary>
public sealed class FileActivityDisplaySettingsStore : IActivityDisplaySettingsStore
{
    public const int DefaultPageSize = 500;
    public const int MinPageSize = 25;
    public const int MaxPageSize = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int pageSize;

    public FileActivityDisplaySettingsStore(IOptions<StoragePathOptions> storage)
    {
        path = Path.Combine(Path.GetFullPath(storage.Value.DataRoot), "activity-display-settings.json");
        pageSize = ReadPersistedPageSize() ?? DefaultPageSize;
    }

    public int PageSize => pageSize;

    public async Task SetPageSizeAsync(int newPageSize, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(newPageSize, MinPageSize, MaxPageSize);
        await gate.WaitAsync(cancellationToken);
        try
        {
            pageSize = clamped;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, new PersistedPageSize(clamped), JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private int? ReadPersistedPageSize()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedPageSize>(json, JsonOptions);
            return persisted is not null ? Math.Clamp(persisted.PageSize, MinPageSize, MaxPageSize) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PersistedPageSize(int PageSize);
}

public sealed record ActivityDisplaySettingsRequest(int PageSize);
