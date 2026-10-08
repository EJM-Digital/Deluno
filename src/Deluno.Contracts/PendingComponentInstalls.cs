using System.Text.Json;

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

    public static PendingComponentInstalls? Read(string dataRoot)
    {
        try
        {
            var path = PathFor(dataRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var pending = JsonSerializer.Deserialize<PendingComponentInstalls>(File.ReadAllText(path));
            return pending is { Kinds.Count: > 0 } ? pending : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Adds these apps to what is pending (an app already there keeps its place); a later retry time or a person's ask wins.</summary>
    public static PendingComponentInstalls Add(string dataRoot, IEnumerable<string> kinds, DateTimeOffset? retryAfterUtc, bool askedByPerson, string? reason)
    {
        var current = Read(dataRoot);
        var merged = new PendingComponentInstalls(
            (current?.Kinds ?? []).Concat(kinds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            retryAfterUtc is null ? current?.RetryAfterUtc : current?.RetryAfterUtc is { } existing && existing > retryAfterUtc ? existing : retryAfterUtc,
            askedByPerson || current?.AskedByPerson == true,
            reason ?? current?.Reason);
        Write(dataRoot, merged);
        return merged;
    }

    /// <summary>Takes these apps off the list (installed, or no longer wanted); the file goes when the list is empty.</summary>
    public static void Remove(string dataRoot, IEnumerable<string> kinds)
    {
        if (Read(dataRoot) is not { } current)
        {
            return;
        }

        var left = current.Kinds.Where(kind => !kinds.Contains(kind, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (left.Length == 0)
        {
            Clear(dataRoot);
            return;
        }

        Write(dataRoot, current with { Kinds = left });
    }

    /// <summary>A person's ask has been acted on: the apps stay pending, but the picker is not opened again for them.</summary>
    public static void MarkAsked(string dataRoot)
    {
        if (Read(dataRoot) is { AskedByPerson: true } current)
        {
            Write(dataRoot, current with { AskedByPerson = false });
        }
    }

    public static void Clear(string dataRoot)
    {
        try
        {
            File.Delete(PathFor(dataRoot));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left in place: the next read still finds only apps that are not installed.
        }
    }

    private static void Write(string dataRoot, PendingComponentInstalls value)
    {
        try
        {
            var path = PathFor(dataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not remembering it only means the person presses Install again.
        }
    }
}
