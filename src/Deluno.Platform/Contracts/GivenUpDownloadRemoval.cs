using Deluno.Jobs.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>
/// What giving up on a release takes out of the way. A person who asks to search for another release has given
/// up on the download Deluno sent: it is still in the client, so it counts as on its way and a new search is
/// skipped. This removes it from the client with its files and closes its record, so the search can go ahead.
/// It only ever acts on a download Deluno sent.
/// </summary>
public interface IGivenUpDownloadRemoval
{
    /// <summary>Removes the download the dispatch describes; <see cref="GivenUpDownloadRemovalResult.Removed"/> says whether it is gone.</summary>
    Task<GivenUpDownloadRemovalResult> RemoveAsync(DownloadDispatchItem dispatch, CancellationToken cancellationToken);
}

/// <param name="Removed">The download is out of the client, with its files, and its record is closed.</param>
/// <param name="Message">Why not, in plain words, when it could not be removed.</param>
public sealed record GivenUpDownloadRemovalResult(bool Removed, string? Message);
