using System.Text.Json;
using Deluno.Api;
using Deluno.Api.Updates;

namespace Deluno.Tray;

/// <summary>
/// The note the Windows tray leaves so the new build knows it was just updated (#1376). The old build writes it with the
/// version it is leaving just before it hands over to the installer; the install's own after-update hook writes it too
/// (with no "from") for an old build that did not. The server reads it once at start, records "Deluno updated from X to Y"
/// and the applied version, and removes it.
/// </summary>
public static class UpdateAppliedNote
{
    private sealed record Note(string? From, string To);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Written just before an update is handed to the installer, by the build that is going away.</summary>
    public static void WriteBeforeApply(string fromVersion, string toVersion)
        => Write(new Note(fromVersion, toVersion), overwriteSameTarget: true);

    /// <summary>Written by the install's after-update hook; keeps a note that already says where it came from.</summary>
    public static void WriteAfterUpdate(string toVersion)
    {
        string? from = null;
        try
        {
            var appFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetDirectoryName(appFolder);
            if (root is not null) from = PreviousVersionFromPackages(Path.Combine(root, "packages"), toVersion);
        }
        catch
        {
            // No guess; the note then carries only the new version.
        }

        Write(new Note(from, toVersion), overwriteSameTarget: false);
    }

    /// <summary>Best guess at the version an update replaced, from the full packages Velopack keeps beside the install.</summary>
    public static string? PreviousVersionFromPackages(string packagesFolder, string toVersion)
    {
        try
        {
            if (!Directory.Exists(packagesFolder)) return null;
            string? best = null;
            foreach (var file in Directory.EnumerateFiles(packagesFolder, "*-full.nupkg"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // Deluno-1.2.3-rc.4-win-full
                var stem = name.EndsWith("-full", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
                var dash = stem.IndexOf('-');
                if (dash < 0) continue;
                var version = stem[(dash + 1)..];
                var channel = version.LastIndexOf("-win", StringComparison.OrdinalIgnoreCase);
                if (channel > 0) version = version[..channel];
                if (string.Equals(version, toVersion, StringComparison.OrdinalIgnoreCase)) continue;
                if (best is null || DelunoVersion.IsNewer(version, best)) best = version;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static void Write(Note note, bool overwriteSameTarget)
    {
        try
        {
            var path = AppSettings.UpdateAppliedNotePath();
            if (!overwriteSameTarget && Read(path) is { } existing && DelunoVersion.Same(existing.ToVersion, note.To))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(note, Json));
        }
        catch
        {
            // Never get in the way of an update; the server then works from the version it recorded last.
        }
    }

    internal static AppliedUpdateHint? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var note = JsonSerializer.Deserialize<Note>(File.ReadAllText(path), Json);
            return string.IsNullOrWhiteSpace(note?.To) ? null : new AppliedUpdateHint(note.From, note.To);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Hands the server the note above, once.</summary>
public sealed class TrayAppliedUpdateHintSource : IAppliedUpdateHintSource
{
    public AppliedUpdateHint? Peek() => UpdateAppliedNote.Read(AppSettings.UpdateAppliedNotePath());

    public void Consume()
    {
        try { File.Delete(AppSettings.UpdateAppliedNotePath()); } catch { /* a leftover note is ignored the next start */ }
    }
}
