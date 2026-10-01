namespace Deluno.Contracts;

/// <summary>
/// Why Deluno started a download (#1098), recorded on the dispatch the moment it is
/// sent to a client and shown as a small tag on the Live journey. It is only ever set
/// from a fact the code already holds: who asked, which request or list the title came
/// from, whether the title already has a file. Where nothing says why it stays null
/// and no tag is shown; it is never guessed.
/// </summary>
public static class DownloadStartedBy
{
    /// <summary>A person chose the release, searched for the title, or added the title by hand.</summary>
    public const string Manual = "manual";

    /// <summary>The title came in as a Seerr request.</summary>
    public const string Seerr = "seerr";

    /// <summary>The title came in from a list (Trakt, MDBList and so on).</summary>
    public const string List = "list";

    /// <summary>An automatic search for something the library does not have.</summary>
    public const string Missing = "missing";

    /// <summary>An automatic search for something better than the file the library has.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>
    /// Not a stored reason: a re-grab of a title Deluno already fetched (a recovery retry, a
    /// refused release replaced, a lost file) keeps the reason of the title's last download.
    /// </summary>
    public const string Inherit = "inherit";

    /// <summary>Whether a stored word is one of the five reasons.</summary>
    public static bool IsKnown(string? value)
        => value is Manual or Seerr or List or Missing or Upgrade;

    /// <summary>The five reasons are stored in lower case; anything else is not a reason.</summary>
    public static string? Normalize(string? value)
    {
        var word = value?.Trim().ToLowerInvariant();
        return IsKnown(word) ? word : null;
    }

    /// <summary>
    /// The reason an automatic or person-started search carries, from how the search was
    /// triggered. <paramref name="hasFile"/> is whether the title already has a file: a
    /// search for a title that has one is an upgrade, one for a title that has none is a
    /// missing one (an RSS match is told apart the same way). A Seerr or list origin is
    /// applied later by the store, which can see where the title came from.
    /// </summary>
    public static string ForSearch(string? triggeredBy, bool hasTarget, bool hasFile)
    {
        var trigger = triggeredBy?.Trim().ToLowerInvariant() ?? string.Empty;
        // A person added the title, or asked for this one title's search or season.
        if (trigger is "movie-added" or "series-added" or "manual-season"
            || (trigger == "manual" && hasTarget))
        {
            return Manual;
        }

        // A re-grab after something happened to an earlier download of the title.
        if (trigger is "recovery-retry" or "grab-rejected" or "health-replacement" or "external-redownload"
            or "download-integrity-failed" or "season-pack-partial-import")
        {
            return Inherit;
        }

        return hasFile ? Upgrade : Missing;
    }

    /// <summary>
    /// The words on the tag. <paramref name="detail"/> is the list's provider name when
    /// the reason is a list; <paramref name="mediaType"/> tells a missing episode from a
    /// missing film. Null when there is no reason to show.
    /// </summary>
    public static string? Label(string? value, string? detail = null, string? mediaType = null)
    {
        switch (Normalize(value))
        {
            case Manual:
                return "You asked";
            case Seerr:
                return "Seerr";
            case List:
                return string.IsNullOrWhiteSpace(detail) ? "From a list" : $"From {detail.Trim()}";
            case Upgrade:
                return "Upgrade hunt";
            case Missing:
                return string.Equals(mediaType, "movies", StringComparison.OrdinalIgnoreCase) ? "Missing" : "Missing episode";
            default:
                return null;
        }
    }
}
