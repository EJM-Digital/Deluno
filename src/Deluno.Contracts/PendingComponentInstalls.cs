

namespace Deluno.Contracts;

/// <summary>
/// The apps Deluno's installer can put on this computer that still have to be installed, written by whichever
/// side learns of it (the tray when GitHub's anonymous limit stopped a lookup; the Companion Tools page when a
/// person pressed Install on an app only the tray's installer can set up) and read by the tray, which offers
/// the picker for them. A small file in the data root, because the tray and the server are separate processes.
/// </summary>
/// <param name="Kinds">The apps, by their kind (<c>nzbget</c>, <c>weir</c>, ...).</param>
/// <param name="RetryAfterUtc">When the cause (GitHub's limit) lifts; null when nothing is waiting on a time.</param>
/// <param name="AskedByPerson">A person pressed Install on the Companion Tools page: the tray opens the picker straight away, once.</param>
/// <param name="Reason">Plain words for why it is pending, shown by the tray.</param>
public sealed record PendingComponentInstalls(IReadOnlyList<string> Kinds, DateTimeOffset? RetryAfterUtc, bool AskedByPerson, string? Reason)
{
    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "config", "pending-component-installs.json");

    private const string What = "The list of apps waiting to be installed";

    /// <summary>What is pending, or null when nothing is, or when the file is busy or damaged. Use <see cref="ReadChecked"/> where "unknown" must not look like "nothing".</summary>
    public static PendingComponentInstalls? Read(string dataRoot, IStateFileSystem? io = null)
        => ReadChecked(dataRoot, io) is { Kind: StateFileKind.Ok, Value: { Kinds.Count: > 0 } pending } ? pending : null;

    /// <summary>The list and how the read went (#1260). Ok with an empty list is "nothing waiting"; Busy and Unreadable are "do not know".</summary>
    public static StateFileRead<PendingComponentInstalls> ReadChecked(string dataRoot, IStateFileSystem? io = null)
        => StateFile.ReadLocked<PendingComponentInstalls>(PathFor(dataRoot), null, io);

    /// <summary>
    /// Adds these apps to what is pending (an app already there keeps its place); a later retry time or a person's ask wins.
    /// Throws <see cref="StateFileException"/>, with the reason in everyday words, when the ask could not be stored: nothing is written then.
    /// </summary>
    public static PendingComponentInstalls Add(string dataRoot, IEnumerable<string> kinds, DateTimeOffset? retryAfterUtc, bool askedByPerson, string? reason, IStateFileSystem? io = null)
        => StateFile.UpdateOrThrow<PendingComponentInstalls>(
            PathFor(dataRoot),
            current => new PendingComponentInstalls(
                (current?.Kinds ?? []).Concat(kinds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                retryAfterUtc is null ? current?.RetryAfterUtc : current?.RetryAfterUtc is { } existing && existing > retryAfterUtc ? existing : retryAfterUtc,
                askedByPerson || current?.AskedByPerson == true,
                reason ?? current?.Reason),
            What,
            io: io)!;

    /// <summary>Takes these apps off the list (installed, or no longer wanted); the file goes when the list is empty. Throws <see cref="StateFileException"/> when it could not be changed.</summary>
    public static void Remove(string dataRoot, IEnumerable<string> kinds, IStateFileSystem? io = null)
        => StateFile.UpdateOrThrow<PendingComponentInstalls>(
            PathFor(dataRoot),
            current =>
            {
                if (current is null)
                {
                    return null;
                }

                var left = current.Kinds.Where(kind => !kinds.Contains(kind, StringComparer.OrdinalIgnoreCase)).ToArray();
                return left.Length == 0 ? null : current with { Kinds = left };
            },
            What,
            io: io);

    /// <summary>A person's ask has been acted on: the apps stay pending, but the picker is not opened again for them. Throws <see cref="StateFileException"/> when it could not be changed.</summary>
    public static void MarkAsked(string dataRoot, IStateFileSystem? io = null)
        => StateFile.UpdateOrThrow<PendingComponentInstalls>(
            PathFor(dataRoot),
            current => current is { AskedByPerson: true } ? current with { AskedByPerson = false } : current,
            What,
            io: io);

    /// <summary>Clears the list. A file that could not be removed is left in place: the next read still finds only apps that are not installed.</summary>
    public static void Clear(string dataRoot, IStateFileSystem? io = null) => StateFile.Delete(PathFor(dataRoot), io);
}
