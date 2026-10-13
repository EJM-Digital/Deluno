using Deluno.Contracts;

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

    /// <summary>The entries and how the read went. A busy or damaged file is never reported as "nothing installed" (#1260); callers that forget things on an empty answer must check this.</summary>
    StateFileRead<IReadOnlyList<InstalledComponentEntry>> ReadChecked() => new(StateFileKind.Ok, Read());

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

public sealed class FileInstalledComponentRecord(string path, TimeProvider? clock = null, IStateFileSystem? io = null) : IInstalledComponentRecord
{
    private const string What = "The list of apps Deluno installed";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "config", "installed-components.json");

    public IReadOnlyList<InstalledComponentEntry> Read() => ReadChecked().Value ?? [];

    /// <summary>The entries with how the read went: a busy or damaged file is not "no apps installed" (#1260).</summary>
    public StateFileRead<IReadOnlyList<InstalledComponentEntry>> ReadChecked()
    {
        var read = StateFile.ReadLocked<List<InstalledComponentEntry>>(path, null, io);
        return new(read.Kind, read.Value?.ToArray(), read.Detail);
    }

    /// <summary>Throws <see cref="StateFileException"/> (in everyday words) when the list is busy or damaged: nothing is written then.</summary>
    public void MarkInstalled(string kind, string host, int port)
        => StateFile.UpdateOrThrow<List<InstalledComponentEntry>>(
            path,
            current =>
            {
                var entries = ToMap(current);
                var wired = entries.TryGetValue(kind, out var existing) ? existing.WiredUtc : null;
                entries[kind] = new InstalledComponentEntry(kind, host, port, _clock.GetUtcNow(), wired);
                return entries.Values.ToList();
            },
            What,
            io: io);

    public void MarkWired(string kind)
        => StateFile.UpdateOrThrow<List<InstalledComponentEntry>>(
            path,
            current =>
            {
                var entries = ToMap(current);
                if (!entries.TryGetValue(kind, out var existing) || existing.WiredUtc is not null)
                {
                    return current;
                }

                entries[kind] = existing with { WiredUtc = _clock.GetUtcNow() };
                return entries.Values.ToList();
            },
            What,
            io: io);

    private static Dictionary<string, InstalledComponentEntry> ToMap(List<InstalledComponentEntry>? list)
        => (list ?? []).GroupBy(entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
}
