using System.Text.Json;

namespace Deluno.Contracts;

/// <summary>What reading one of Deluno's own state files found (#1260).</summary>
public enum StateFileKind
{
    /// <summary>The file was read and its contents understood.</summary>
    Ok,

    /// <summary>There is no file: nothing has been saved yet. The only case that may mean "start empty".</summary>
    Missing,

    /// <summary>Another thread or process held the file (a sharing violation, or access denied while it is replaced) for the whole wait. Says nothing about what is in it.</summary>
    Busy,

    /// <summary>The file is there but its contents cannot be understood: cut short, empty, or not what was written.</summary>
    Unreadable
}

/// <summary>The answer of <see cref="StateFile.Read{T}"/>. <see cref="Value"/> is set only when <see cref="Kind"/> is Ok.</summary>
public sealed record StateFileRead<T>(StateFileKind Kind, T? Value, string? Detail = null) where T : class
{
    public bool IsOk => Kind == StateFileKind.Ok;

    /// <summary>True when the file could be read now or is simply not there yet: the two answers a read-modify-write may go on from.</summary>
    public bool CanBuildOn => Kind is StateFileKind.Ok or StateFileKind.Missing;

    /// <summary>The value, or <paramref name="fallback"/> when there is none. Never use this to start a write from a Busy or Unreadable file.</summary>
    public T ValueOr(T fallback) => Value ?? fallback;
}

/// <summary>What an update of a state file did. <see cref="Problem"/> is in everyday words, and set exactly when nothing was written.</summary>
public sealed record StateFileUpdate<T>(bool Landed, T? Value, string? Problem) where T : class;

/// <summary>A state file could not be read or written. A kind of <see cref="IOException"/>, so code that already waits for a busy file treats it the same; the message is in everyday words.</summary>
public sealed class StateFileException(string message) : IOException(message);

/// <summary>What a read-modify-write does with a file that is there but cannot be understood.</summary>
public enum UnreadableBehavior
{
    /// <summary>A copy is kept and nothing is written: the caller says so. The default, because the file may be all there is.</summary>
    Refuse,

    /// <summary>A copy is kept and the change is built on nothing. Only for a file whose loss costs nothing but its defaults.</summary>
    StartFresh
}

/// <summary>Everything <see cref="StateFile"/> asks of the disk and the clock, so a test can make a file busy or cut short and never waits.</summary>
public interface IStateFileSystem
{
    DateTimeOffset UtcNow { get; }
    bool Exists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string text);

    /// <summary>Moves <paramref name="from"/> over <paramref name="to"/>, replacing it.</summary>
    void Move(string from, string to);
    void Copy(string from, string to);
    void Delete(string path);
    void CreateDirectory(string path);
    IEnumerable<string> EnumerateFiles(string directory, string pattern);

    /// <summary>Takes the cross-process lock for a file; null when another holder has it. Dispose to let go. The OS lets go of it if the holder dies.</summary>
    IDisposable? TryLock(string lockPath);
    void Sleep(TimeSpan time);
}

/// <summary>The disk. A lock is a file opened for no sharing, which works between the tray and the server and between two copies in one process.</summary>
public sealed class RealStateFileSystem : IStateFileSystem
{
    public static readonly RealStateFileSystem Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public bool Exists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);
    public void Move(string from, string to) => File.Move(from, to, overwrite: true);
    public void Copy(string from, string to) => File.Copy(from, to, overwrite: false);
    public void Delete(string path) => File.Delete(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];

    public IDisposable? TryLock(string lockPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Sleep(TimeSpan time) => Thread.Sleep(time);
}

/// <summary>
/// The one way Deluno reads and writes its own small JSON state files (#1260): settings, saved sign-ins, the list of installed
/// apps, what is waiting to be installed, the recycle bin's state, and the like.
///
/// <para><b>Reading.</b> A read has four answers (<see cref="StateFileKind"/>). A file another thread or process is writing or
/// replacing (a sharing violation, or access denied while the old one waits to be deleted) is waited for, for about a quarter of
/// a second, and then reported Busy: that says nothing about what is in it. Contents that cannot be understood are Unreadable.
/// Neither is ever turned into "empty" or "defaults" by a caller that is going to write the file back.</para>
///
/// <para><b>Writing.</b> A new copy is written beside the file and renamed over it, so a reader sees the old file or the new one
/// and never half of one, and a process killed mid-write leaves the old file whole. A busy rename is retried for about two
/// seconds, and a write that still cannot land says why instead of pretending.</para>
///
/// <para><b>Read-modify-write.</b> <see cref="Update{T}"/> holds a lock on a file beside the state file for the whole read, change
/// and write, so two threads, or the tray and the server in service mode, cannot lose each other's change. A change that cannot
/// start from the file's true contents does not happen, and the caller is told why.</para>
/// </summary>
public static class StateFile
{
    /// <summary>A read tries this many times, 10 ms apart: about 250 ms of waiting at most.</summary>
    public const int ReadAttempts = 26;

    public static readonly TimeSpan ReadPause = TimeSpan.FromMilliseconds(10);

    /// <summary>A write tries this many times, 50 ms apart: about 2 seconds of waiting at most.</summary>
    public const int WriteAttempts = 40;

    public static readonly TimeSpan WritePause = TimeSpan.FromMilliseconds(50);

    /// <summary>The lock is tried this many times, 25 ms apart: about 2.5 seconds of waiting at most.</summary>
    public const int LockAttempts = 100;

    public static readonly TimeSpan LockPause = TimeSpan.FromMilliseconds(25);

    public static string LockPathFor(string path) => path + ".lock";

    /// <summary>Reads and understands the file. Never throws for a busy, missing or damaged file.</summary>
    public static StateFileRead<T> Read<T>(string path, JsonSerializerOptions? json = null, IStateFileSystem? io = null) where T : class
    {
        io ??= RealStateFileSystem.Instance;
        string text;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!io.Exists(path))
                {
                    return new(StateFileKind.Missing, null);
                }

                text = io.ReadAllText(path);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(StateFileKind.Missing, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being written, replaced, or waiting to be deleted: none of that says what is in it. Look again for a little while.
                if (attempt >= ReadAttempts)
                {
                    return new(StateFileKind.Busy, null, ex.Message);
                }

                io.Sleep(ReadPause);
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new(StateFileKind.Unreadable, null, "the file is empty");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(text, json);
            return value is null
                ? new(StateFileKind.Unreadable, null, "the file holds no value")
                : new(StateFileKind.Ok, value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new(StateFileKind.Unreadable, null, ex.Message);
        }
    }

    /// <summary>
    /// Reads under the same lock a writer holds, so the read never lands between a writer's read and its rename.
    /// A lock that cannot be had is Busy.
    /// </summary>
    public static StateFileRead<T> ReadLocked<T>(string path, JsonSerializerOptions? json = null, IStateFileSystem? io = null) where T : class
    {
        io ??= RealStateFileSystem.Instance;
        if (!io.Exists(path))
        {
            return new(StateFileKind.Missing, null);
        }

        using var held = AcquireLock(path, io);
        return held is null
            ? new(StateFileKind.Busy, null, "another part of Deluno held the file's lock")
            : Read<T>(path, json, io);
    }

    /// <summary>Writes the text beside the file and renames it over. Null when it landed, else why not. Never throws.</summary>
    public static string? Write(string path, string text, IStateFileSystem? io = null, bool quick = false)
    {
        io ??= RealStateFileSystem.Instance;
        var attempts = quick ? 1 : WriteAttempts;
        string? last = null;
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    io.CreateDirectory(folder);
                }

                io.WriteAllText(temporary, text);
                io.Move(temporary, path);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by a reader, a scanner or a pending delete: the rename waits its turn.
                last = ex.Message;
                if (attempt < attempts)
                {
                    io.Sleep(WritePause);
                }
            }
            catch (Exception ex)
            {
                last = ex.Message;
                break;
            }
        }

        DiscardTemporary(temporary, io);
        return last ?? "the file could not be written";
    }

    public static string? WriteJson<T>(string path, T value, JsonSerializerOptions? json = null, IStateFileSystem? io = null)
        => Write(path, JsonSerializer.Serialize(value, json), io);

    /// <summary>Deletes the file, waiting out a busy one. Null when it is gone, else why not. A missing file is gone.</summary>
    public static string? Delete(string path, IStateFileSystem? io = null)
    {
        io ??= RealStateFileSystem.Instance;
        string? last = null;
        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            try
            {
                io.Delete(path);
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
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
        }

        return last;
    }

    /// <summary>
    /// Reads the file, applies <paramref name="change"/> to what is in it (null when nothing is saved yet), and writes the result,
    /// all under the file's lock. <paramref name="change"/> returning null deletes the file.
    /// Nothing is written, and <see cref="StateFileUpdate{T}.Problem"/> says why, when the lock or the file stayed busy, or when the
    /// file is unreadable and <paramref name="whenUnreadable"/> is <see cref="UnreadableBehavior.Refuse"/>.
    /// </summary>
    /// <param name="what">The file in everyday words, for the problem text ("Deluno's saved sign-ins").</param>
    public static StateFileUpdate<T> Update<T>(
        string path,
        Func<T?, T?> change,
        string what,
        JsonSerializerOptions? json = null,
        IStateFileSystem? io = null,
        UnreadableBehavior whenUnreadable = UnreadableBehavior.Refuse) where T : class
    {
        io ??= RealStateFileSystem.Instance;
        using var held = AcquireLock(path, io);
        if (held is null)
        {
            return new(false, null, $"{what} {BusyWords}");
        }

        var current = Read<T>(path, json, io);
        T? basis = current.Value;
        switch (current.Kind)
        {
            case StateFileKind.Busy:
                return new(false, null, $"{what} {BusyWords}");
            case StateFileKind.Unreadable:
                var kept = KeepUnreadableCopy(path, io);
                if (whenUnreadable == UnreadableBehavior.Refuse)
                {
                    return new(false, null, UnreadableWords(what, kept));
                }

                basis = null;
                break;
        }

        var next = change(basis);
        if (next is null)
        {
            var gone = Delete(path, io);
            return gone is null ? new(true, null, null) : new(false, null, $"{what} could not be cleared: {gone}.");
        }

        var failed = Write(path, JsonSerializer.Serialize(next, json), io);
        return failed is null
            ? new(true, next, null)
            : new(false, null, $"{what} could not be saved: {failed}.");
    }

    /// <summary>Update, but a change that did not land throws <see cref="StateFileException"/> with the plain reason.</summary>
    public static T? UpdateOrThrow<T>(
        string path,
        Func<T?, T?> change,
        string what,
        JsonSerializerOptions? json = null,
        IStateFileSystem? io = null,
        UnreadableBehavior whenUnreadable = UnreadableBehavior.Refuse) where T : class
    {
        var result = Update(path, change, what, json, io, whenUnreadable);
        return result.Landed ? result.Value : throw new StateFileException(result.Problem ?? $"{what} could not be saved.");
    }

    /// <summary>
    /// Keeps a timestamped copy of a file that could not be understood, beside it, unless the same contents are already kept.
    /// Returns the copy's path, or null when none could be made. Never throws.
    /// </summary>
    public static string? KeepUnreadableCopy(string path, IStateFileSystem? io = null)
    {
        io ??= RealStateFileSystem.Instance;
        try
        {
            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileName(path);
            string? text = null;
            try
            {
                text = io.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Copied byte for byte below.
            }

            foreach (var existing in io.EnumerateFiles(folder, name + ".unreadable-*"))
            {
                try
                {
                    if (text is not null && io.ReadAllText(existing) == text)
                    {
                        return existing;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A copy that cannot be read is not proof of this one.
                }
            }

            var target = Path.Combine(folder, $"{name}.unreadable-{io.UtcNow:yyyyMMdd-HHmmss}");
            if (io.Exists(target))
            {
                target = Path.Combine(folder, $"{name}.unreadable-{io.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            }

            io.Copy(path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string UnreadableWords(string what, string? keptCopy)
        => keptCopy is null
            ? $"{what} could not be read (the file is damaged), so nothing was changed."
            : $"{what} could not be read (the file is damaged), so nothing was changed. A copy of the damaged file was kept as {Path.GetFileName(keptCopy)}.";

    private const string BusyWords = "is in use by something else right now, so nothing was changed. Try again in a moment.";

    private static IDisposable? AcquireLock(string path, IStateFileSystem io)
    {
        var lockPath = LockPathFor(path);
        for (var attempt = 1; attempt <= LockAttempts; attempt++)
        {
            var held = io.TryLock(lockPath);
            if (held is not null)
            {
                return held;
            }

            if (attempt < LockAttempts)
            {
                io.Sleep(LockPause);
            }
        }

        return null;
    }

    private static void DiscardTemporary(string path, IStateFileSystem io)
    {
        try
        {
            io.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file costs nothing.
        }
    }
}
