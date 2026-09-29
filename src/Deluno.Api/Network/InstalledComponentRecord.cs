using System.Text.Json;

namespace Deluno.Api.Network;

/// <summary>
/// One app the installer's picker put on this computer, and whether Deluno has
/// connected it yet (#1054). <see cref="WiredUtc"/> is set once, after the first
/// attempt, whatever its outcome: a person who later deletes the connection must
/// not find it made again, and a failed test is reported, not retried forever.
/// </summary>
public sealed record InstalledComponentEntry(string Kind, string Host, int Port, DateTimeOffset InstalledUtc, DateTimeOffset? WiredUtc = null);

/// <summary>
/// The tray (which installs) writes an entry when an app finishes installing; the
/// running Deluno reads the unwired ones and connects them, then marks them wired.
/// A plain JSON file under the data root, with no secrets in it (the sign-ins
/// live in <see cref="IBundledComponentCredentialStore"/>), so it works whether or
/// not anyone has finished first run.
/// </summary>
public interface IInstalledComponentRecord
{
    IReadOnlyList<InstalledComponentEntry> Read();

    /// <summary>Records that <paramref name="kind"/> was installed by the picker. An earlier wired mark is kept.</summary>
    void MarkInstalled(string kind, string host, int port);

    void MarkWired(string kind);
}

public sealed class NoInstalledComponentRecord : IInstalledComponentRecord
{
    public IReadOnlyList<InstalledComponentEntry> Read() => [];
    public void MarkInstalled(string kind, string host, int port) { }
    public void MarkWired(string kind) { }
}

public sealed class FileInstalledComponentRecord(string path, TimeProvider? clock = null) : IInstalledComponentRecord
{
    private static readonly object Gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "config", "installed-components.json");

    public IReadOnlyList<InstalledComponentEntry> Read()
    {
        lock (Gate)
        {
            return Load().Values.ToArray();
        }
    }

    public void MarkInstalled(string kind, string host, int port)
    {
        lock (Gate)
        {
            var entries = Load();
            var wired = entries.TryGetValue(kind, out var existing) ? existing.WiredUtc : null;
            entries[kind] = new InstalledComponentEntry(kind, host, port, _clock.GetUtcNow(), wired);
            Save(entries);
        }
    }

    public void MarkWired(string kind)
    {
        lock (Gate)
        {
            var entries = Load();
            if (entries.TryGetValue(kind, out var existing) && existing.WiredUtc is null)
            {
                entries[kind] = existing with { WiredUtc = _clock.GetUtcNow() };
                Save(entries);
            }
        }
    }

    private Dictionary<string, InstalledComponentEntry> Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<List<InstalledComponentEntry>>(File.ReadAllText(path));
                if (parsed is not null)
                {
                    return parsed.ToDictionary(entry => entry.Kind, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // An unreadable record reads as empty: nothing is wired from it.
        }

        return new Dictionary<string, InstalledComponentEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, InstalledComponentEntry> entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(entries.Values.ToArray()));
    }
}
