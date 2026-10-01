using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// The Activity row written when retention removes a finished torrent from a download client once it had
/// shared enough (#1098). The sentence is the one place it is said, in plain words; the Live screen counts
/// these rows for "cleaned up today".
/// </summary>
public static partial class SeedingCleanupActivity
{
    public const string Category = "download.sharing.reclaimed";

    /// <summary>
    /// "Removed Big Buck Bunny (2008) from Deluge once it had shared enough". The title is the library's own when the
    /// download is matched to one (see <see cref="LibraryTitle"/>), the release name only when it is not, and the client
    /// is called by its own short name ("Deluge"), never "Deluge on HOST".
    /// </summary>
    public static string Message(string title, string clientName)
        => $"Removed {title} from {ClientShortName(clientName)} once it had shared enough · your copy stays in the library";

    /// <summary>A client's name without the machine it runs on: "Deluge on WIN-PC" is "Deluge".</summary>
    public static string ClientShortName(string clientName)
    {
        var name = clientName.Trim();
        var shortName = HostSuffix().Replace(name, string.Empty).Trim();
        return shortName.Length > 0 ? shortName : name;
    }

    /// <summary>The library's title for the sentence: "Big Buck Bunny (2008)", or the plain title when there is no year (or it already carries it).</summary>
    public static string LibraryTitle(string title, int? year)
    {
        var name = title.Trim();
        if (year is null or <= 0 || name.EndsWith($"({year})", StringComparison.Ordinal))
        {
            return name;
        }

        return $"{name} ({year})";
    }

    [GeneratedRegex(@"\s+on\s+\S+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostSuffix();
}
