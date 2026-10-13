using Deluno.Contracts;

namespace Deluno.Tests.Support;

/// <summary>
/// The real disk with faults a test can switch on (#1260): a read, write, rename or delete that fails the way a busy file
/// does (a sharing violation, or access denied while a delete is pending), a lock another process holds, and a clock. Every
/// pause is recorded and none waits, so a test of "tries for about a quarter of a second" takes no time.
/// Shared by the test projects through a linked file.
/// </summary>
public sealed class ScriptedStateFileSystem : IStateFileSystem
{
    private readonly object _gate = new();
    private int _reads, _writes, _moves, _deletes;

    public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    /// <summary>Decides, per call, whether a read fails: given the path and how many reads have been made, returns the exception or null.</summary>
    public Func<string, int, Exception?>? ReadFault { get; set; }

    public Func<string, int, Exception?>? WriteFault { get; set; }

    public Func<string, int, Exception?>? MoveFault { get; set; }

    public Func<string, int, Exception?>? DeleteFault { get; set; }

    /// <summary>When true, the cross-process lock is refused, as if another process held it.</summary>
    public bool LockHeldByAnother { get; set; }

    /// <summary>Called once when a pause is asked for, so a test can let a "busy" file clear after N pauses.</summary>
    public Action<int>? OnSleep { get; set; }

    public List<TimeSpan> Sleeps { get; } = [];

    public int Reads { get { lock (_gate) { return _reads; } } }

    public int Moves { get { lock (_gate) { return _moves; } } }

    public DateTimeOffset UtcNow => Now;

    public static IOException SharingViolation() => new("The process cannot access the file because it is being used by another process.");

    public static UnauthorizedAccessException AccessDenied() => new("Access to the path is denied.");

    public bool Exists(string path) => File.Exists(path);

    public string ReadAllText(string path)
    {
        int n;
        lock (_gate) { n = ++_reads; }
        if (ReadFault?.Invoke(path, n) is { } fault) { throw fault; }
        return File.ReadAllText(path);
    }

    public void WriteAllText(string path, string text)
    {
        int n;
        lock (_gate) { n = ++_writes; }
        if (WriteFault?.Invoke(path, n) is { } fault) { throw fault; }
        File.WriteAllText(path, text);
    }

    public void Move(string from, string to)
    {
        int n;
        lock (_gate) { n = ++_moves; }
        if (MoveFault?.Invoke(to, n) is { } fault) { throw fault; }
        File.Move(from, to, overwrite: true);
    }

    public void Copy(string from, string to) => File.Copy(from, to, overwrite: false);

    public void Delete(string path)
    {
        int n;
        lock (_gate) { n = ++_deletes; }
        if (DeleteFault?.Invoke(path, n) is { } fault) { throw fault; }
        File.Delete(path);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];

    public IDisposable? TryLock(string lockPath) => LockHeldByAnother ? null : RealStateFileSystem.Instance.TryLock(lockPath);

    public void Sleep(TimeSpan time)
    {
        int count;
        lock (_gate)
        {
            Sleeps.Add(time);
            count = Sleeps.Count;
        }

        OnSleep?.Invoke(count);
    }
}
