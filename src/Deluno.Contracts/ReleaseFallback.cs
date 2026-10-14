using System.Net;

namespace Deluno.Contracts;

/// <summary>What one release came to when its files were looked for: found, passed over with a reason, or not readable right now.</summary>
public sealed record ReleaseAttempt<T>(T? Value, string? SkipReason, bool Unreadable)
{
    public static ReleaseAttempt<T> Found(T value) => new(value, null, false);

    public static ReleaseAttempt<T> Skip(string reason) => new(default, reason, false);

    public static ReleaseAttempt<T> CouldNotRead() => new(default, null, true);
}

/// <summary>
/// What a walk over a repository's newest releases came to. <see cref="Unreadable"/> is a call that failed for any reason
/// but "the file is not there", so the caller tries its other route; it is not "no release has its files".
/// </summary>
public sealed record ReleaseWalk<T>(T? Value, string? Tag, IReadOnlyList<string> Skipped, string? NewestLooked, bool Unreadable)
{
    public bool Found => Tag is not null;
}

/// <summary>The installer's address and the checksum read for it from the release's own checksum file (null when the component publishes none).</summary>
public sealed record ReleaseFiles(string AssetUrl, string? Sha256);

/// <summary>
/// One walk over a repository's newest releases, shared by the tray's component picker and the update tool inside
/// Deluno (#1263, #1264). A release is published before its files finish uploading (Weir 1.0.0-rc.17 had none), so the
/// newest release is not always the newest one with its files: the walk takes the newest that has them, looks back over
/// at most <see cref="MaxReleasesLooked"/> releases, and keeps why each newer one was passed over.
/// </summary>
public static class ReleaseFallback
{
    /// <summary>
    /// How many of a repository's newest releases are looked at before saying none has its installer yet. The picker's
    /// anonymous budget is about six calls: the release feed, then one checksum file per release looked at.
    /// </summary>
    public const int MaxReleasesLooked = 5;

    /// <summary>Why a release is passed over after its installer download answered 404 (the checksum file was uploaded before the installer).</summary>
    public static string InstallerNotThere(string tag) => $"{tag}'s installer download was not there yet";

    /// <summary>The one plain sentence for "no release in the window has its files", naming the newest release looked at.</summary>
    public static string NoInstallerYetSentence(string displayName, string? newestLooked)
        => newestLooked is null
            ? $"{displayName}'s newest releases have no Windows installer yet; try again in a few minutes."
            : $"{displayName}'s newest releases have no Windows installer yet (the newest, {newestLooked}, does not have all its files); try again in a few minutes.";

    /// <summary>
    /// Tries the tags newest first until <paramref name="tryTag"/> finds one. A tag in <paramref name="excludeTags"/> (already
    /// chosen once, its installer download answered 404) still counts toward the cap and costs no call.
    /// </summary>
    public static async Task<ReleaseWalk<T>> WalkAsync<T>(
        IEnumerable<string> tagsNewestFirst,
        IReadOnlyCollection<string>? excludeTags,
        Func<string, Task<ReleaseAttempt<T>>> tryTag,
        int maxReleases = MaxReleasesLooked)
    {
        var skipped = new List<string>();
        string? newest = null;
        foreach (var tag in tagsNewestFirst.Take(maxReleases))
        {
            newest ??= tag;
            if (excludeTags?.Contains(tag) == true)
            {
                skipped.Add(InstallerNotThere(tag));
                continue;
            }

            var attempt = await tryTag(tag);
            if (attempt.Unreadable)
            {
                return new ReleaseWalk<T>(default, null, skipped, newest, Unreadable: true);
            }

            if (attempt.Value is not null)
            {
                return new ReleaseWalk<T>(attempt.Value, tag, skipped, newest, Unreadable: false);
            }

            skipped.Add(attempt.SkipReason ?? $"{tag} has no files yet");
        }

        return new ReleaseWalk<T>(default, null, skipped, newest, Unreadable: false);
    }

    /// <summary>
    /// One release's files from its download folder, which costs no API call: the checksum file first (its absence means the
    /// release has no files yet; a checksum file that does not list the installer passes the release over), or, for a
    /// component that publishes no checksum, a HEAD on the installer. With <paramref name="probeInstaller"/> a checksum is
    /// followed by a HEAD on the installer too, because the checksum file can be uploaded before the installer.
    /// </summary>
    public static async Task<ReleaseAttempt<ReleaseFiles>> TryDownloadFolderAsync(
        HttpClient http,
        string repository,
        string tag,
        string installerAsset,
        string? checksumAsset,
        Func<string, string, string?> parseChecksum,
        bool probeInstaller,
        CancellationToken cancellationToken)
    {
        var assetUrl = ReleaseAtomFeed.DownloadUrl(repository, tag, installerAsset);
        string? sha256 = null;
        if (!string.IsNullOrWhiteSpace(checksumAsset))
        {
            using var sums = await http.GetAsync(ReleaseAtomFeed.DownloadUrl(repository, tag, checksumAsset), cancellationToken);
            if (sums.StatusCode == HttpStatusCode.NotFound)
            {
                return ReleaseAttempt<ReleaseFiles>.Skip($"{tag} has no {checksumAsset} yet");
            }

            if (!sums.IsSuccessStatusCode)
            {
                return ReleaseAttempt<ReleaseFiles>.CouldNotRead();
            }

            sha256 = parseChecksum(await sums.Content.ReadAsStringAsync(cancellationToken), installerAsset);
            if (sha256 is null)
            {
                return ReleaseAttempt<ReleaseFiles>.Skip($"{tag}'s {checksumAsset} does not list {installerAsset}");
            }

            if (!probeInstaller)
            {
                return ReleaseAttempt<ReleaseFiles>.Found(new ReleaseFiles(assetUrl, sha256));
            }
        }

        // Without a checksum to read (or to be sure the installer is there after it), ask for the file itself.
        using var head = new HttpRequestMessage(HttpMethod.Head, assetUrl);
        using var exists = await http.SendAsync(head, cancellationToken);
        if (exists.StatusCode == HttpStatusCode.NotFound)
        {
            return ReleaseAttempt<ReleaseFiles>.Skip(sha256 is null ? $"{tag} has no {installerAsset} yet" : InstallerNotThere(tag));
        }

        return exists.IsSuccessStatusCode
            ? ReleaseAttempt<ReleaseFiles>.Found(new ReleaseFiles(assetUrl, sha256))
            : ReleaseAttempt<ReleaseFiles>.CouldNotRead();
    }
}
