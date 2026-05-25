using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Deluno.Downloader.Torrent.Engine;

/// <summary>
/// JSON-file backed <see cref="ITorrentEngineConfigStore"/>. Writes are
/// atomic (write-to-temp-then-rename) so a crash mid-save can't leave
/// a half-written file that fails to parse on next startup.
///
/// File path is <c>&lt;dataRoot&gt;/downloader/torrent-config.json</c>.
/// </summary>
public sealed class FileBackedTorrentEngineConfigStore : ITorrentEngineConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly ILogger<FileBackedTorrentEngineConfigStore> _logger;

    public FileBackedTorrentEngineConfigStore(
        string filePath, ILogger<FileBackedTorrentEngineConfigStore> logger)
    {
        _path = filePath;
        _logger = logger;
    }

    public async Task<TorrentEngineConfig> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation(
                "No torrent-engine config at {Path}; using built-in defaults.", _path);
            return TorrentEngineConfig.Defaults;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var config = await JsonSerializer.DeserializeAsync<TorrentEngineConfig>(
                stream, JsonOptions, ct).ConfigureAwait(false);
            return config ?? TorrentEngineConfig.Defaults;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not parse torrent-engine config at {Path}; falling back to defaults. " +
                "Saving any future change will overwrite the malformed file.", _path);
            return TorrentEngineConfig.Defaults;
        }
    }

    public async Task SaveAsync(TorrentEngineConfig config, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomic write: write to a temp file in the same dir then
        // File.Move to overwrite the real path. Means a crash during
        // write never leaves a corrupted config file readable.
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, ct)
                .ConfigureAwait(false);
        }
        File.Move(tempPath, _path, overwrite: true);
        _logger.LogInformation("Torrent-engine config saved to {Path}.", _path);
    }
}
