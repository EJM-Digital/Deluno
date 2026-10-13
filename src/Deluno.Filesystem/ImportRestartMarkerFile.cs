using System.Collections.Concurrent;
using System.Text.Json;

namespace Deluno.Filesystem;

/// <summary>What reading an import restart marker found (#1259).</summary>
public enum ImportRestartMarkerKind
{
    /// <summary>The marker was read and names the import it guards.</summary>
    Ok,

    /// <summary>There is no marker file (it was just removed, or never written).</summary>
    Missing,

    /// <summary>
    /// Another program holds the marker (a media server, antivirus or sync client), or it is waiting to be deleted.
    /// This says nothing about what is in it: never act on it, look again later.
    /// </summary>
    Busy,

    /// <summary>The marker was read but is truncated or not a marker. Only this means the content cannot be trusted.</summary>
    Unreadable
}

public sealed record ImportRestartMarkerRead(ImportRestartMarkerKind Kind, ImportRestartMarker? Marker);

/// <summary>The few file operations a marker needs, so a test can make one of them busy or refused.</summary>
public interface IImportRestartMarkerIo
{
    bool Exists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string text);

    void Move(string from, string to);

    void Sleep(TimeSpan pause);
}

internal sealed class RealImportRestartMarkerIo : IImportRestartMarkerIo
{
    public static readonly RealImportRestartMarkerIo Instance = new();

    public bool Exists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);

    public void Move(string from, string to) => File.Move(from, to, overwrite: true);

    public void Sleep(TimeSpan pause) => Thread.Sleep(pause);
}

/// <summary>
/// Reads and writes the <c>*.deluno-marker.json</c> next to an import's staging file (#913, #1259). The marker sits
/// inside a library folder where media servers, antivirus and sync clients open files, so a marker that cannot be
/// opened right now is told apart from one that is damaged, the way the tray's start marker does (#1256). For a
/// "move" import the staging file is the only copy of the media, so a marker that is merely busy must never be
/// taken for a bad one.
/// </summary>
public static class ImportRestartMarkerFile
{
    public const string Suffix = ".deluno-marker.json";

    /// <summary>A read tries this many times, 10 ms apart: about 250 ms of waiting at most.</summary>
    internal const int ReadAttempts = 26;

    internal static readonly TimeSpan ReadPause = TimeSpan.FromMilliseconds(10);

    /// <summary>A write tries this many times, 50 ms apart: about a second of waiting at most.</summary>
    internal const int WriteAttempts = 20;

    internal static readonly TimeSpan WritePause = TimeSpan.FromMilliseconds(50);

    private static readonly ConcurrentDictionary<string, byte> InFlight = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The staging file an import in this process is working on right now: a recovery pass leaves it alone.</summary>
    public static void MarkInFlight(string stagingPath) => InFlight[Path.GetFullPath(stagingPath)] = 0;

    public static void ClearInFlight(string stagingPath) => InFlight.TryRemove(Path.GetFullPath(stagingPath), out _);

    public static bool IsInFlight(string stagingPath) => InFlight.ContainsKey(Path.GetFullPath(stagingPath));

    public static string PathFor(string stagingPath) => stagingPath + Suffix;

    public static ImportRestartMarkerRead Read(string path) => Read(path, RealImportRestartMarkerIo.Instance);

    public static ImportRestartMarkerRead Read(string path, IImportRestartMarkerIo io)
    {
        string text;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!io.Exists(path))
                {
                    return new(ImportRestartMarkerKind.Missing, null);
                }

                text = io.ReadAllText(path);
                break;
            }
            catch (FileNotFoundException)
            {
                return new(ImportRestartMarkerKind.Missing, null);
            }
            catch (DirectoryNotFoundException)
            {
                return new(ImportRestartMarkerKind.Missing, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by another program (sharing violation) or waiting to be deleted (access denied):
                // neither says anything about what is in it. Look again for a little while.
                if (attempt >= ReadAttempts)
                {
                    return new(ImportRestartMarkerKind.Busy, null);
                }

                io.Sleep(ReadPause);
            }
            catch
            {
                return new(ImportRestartMarkerKind.Unreadable, null);
            }
        }

        try
        {
            var marker = JsonSerializer.Deserialize<ImportRestartMarker>(text);
            if (marker is null
                || string.IsNullOrWhiteSpace(marker.SourcePath)
                || string.IsNullOrWhiteSpace(marker.DestinationPath))
            {
                return new(ImportRestartMarkerKind.Unreadable, null);
            }

            return new(ImportRestartMarkerKind.Ok, marker);
        }
        catch (JsonException)
        {
            return new(ImportRestartMarkerKind.Unreadable, null);
        }
    }

    /// <summary>
    /// Writes the marker whole or not at all: a temporary file, then a rename over the marker, so a crash can never
    /// leave half of one. A sharing violation or access denied is tried again for a while. Never throws; null when
    /// it landed, else why not.
    /// </summary>
    public static string? WriteAtomic(string path, ImportRestartMarker marker) => WriteAtomic(path, marker, RealImportRestartMarkerIo.Instance);

    public static string? WriteAtomic(string path, ImportRestartMarker marker, IImportRestartMarkerIo io)
    {
        string? last = null;
        var json = JsonSerializer.Serialize(marker);
        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            try
            {
                var temporary = path + ".tmp";
                io.WriteAllText(temporary, json);
                io.Move(temporary, path);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex.Message;
                if (attempt < WriteAttempts)
                {
                    io.Sleep(WritePause);
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        return last ?? "the marker could not be written";
    }
}
