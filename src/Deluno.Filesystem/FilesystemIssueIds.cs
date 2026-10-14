using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Deluno.Filesystem;

/// <summary>What an issue id carries: the issue's details, so a repair needs no lookup.</summary>
public sealed record FilesystemIssueToken(
    string Kind,
    string MediaType,
    string LibraryId,
    string EntityId,
    string? EpisodeId,
    string Path,
    string? Title = null);

/// <summary>
/// #1326: an issue id is the issue's details as JSON, then base64url. Its length is therefore a function of how it is built,
/// not of a member name, so <see cref="MaxLength"/> works the longest one out from the longest path and title the request
/// limits allow, and the request limits take their limit for the id from it. A limit lower than this refuses an id Deluno itself issued.
/// </summary>
public static class FilesystemIssueIds
{
    public static string Encode(
        string kind, string mediaType, string libraryId, string entityId, string? episodeId, string path, string? title = null)
        => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new FilesystemIssueToken(kind, mediaType, libraryId, entityId, episodeId, path, title))));

    public static FilesystemIssueToken Decode(string issueId)
        => JsonSerializer.Deserialize<FilesystemIssueToken>(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(issueId)))
           ?? throw new InvalidOperationException("Invalid reconciliation issue id.");

    /// <summary>
    /// The longest id this builder can produce when a path (and, for an unmatched file, the name taken from it in the entity
    /// slot) is at most <paramref name="pathChars"/> and a title at most <paramref name="titleChars"/>. The JSON writer may turn
    /// one character into a six-character escape (non-ASCII, quotes, angle brackets), and the fixed members (kind, media type,
    /// two ids, the member names) add a few hundred bytes; base64 then makes every three bytes four characters.
    /// </summary>
    public static int MaxLength(int pathChars, int titleChars)
    {
        const int escapedBytesPerChar = 6;
        const int fixedBytes = 512;
        var json = fixedBytes + escapedBytesPerChar * (2 * pathChars + titleChars);
        return (json + 2) / 3 * 4;
    }
}
