using Deluno.Contracts;

namespace Deluno.Media;

/// <summary>
/// Collects the facts <see cref="AcquisitionBlockerReader"/> reports on, from
/// wherever they happen to live.
///
/// <para>Split from the reader on purpose. The reader turns facts into
/// sentences and is a pure function, so every phrase it produces can be tested
/// without a database; this is the part that has to go and ask, and it is kept
/// deliberately thin so there is little in it that can be wrong.</para>
///
/// <para><b>An unanswerable question is not a blocker.</b> When a download
/// client or a processor cannot be reached, this reports nothing for it rather
/// than guessing. Saying "a client is holding this" when Deluno simply could
/// not ask would send someone to delete a download that was never there.</para>
/// </summary>
public sealed class AcquisitionBlockerGatherer(
    IMediaStateRepository mediaState,
    TimeProvider timeProvider)
{
    /// <param name="clientHoldingRelease">
    /// Supplied by the caller, which is the layer that can see the download
    /// clients. Null when nothing holds it, or when nothing could be asked.
    /// </param>
    /// <param name="processorHoldingFile">
    /// Likewise for a processor hand-off that has not finished.
    /// </param>
    /// <param name="isImportExcluded">
    /// Whether an exclusion covers this title.
    /// </param>
    public async Task<AcquisitionBlockersResponse> GatherAsync(
        MediaKind kind,
        string mediaId,
        string title,
        string? clientHoldingRelease,
        string? processorHoldingFile,
        bool isImportExcluded,
        CancellationToken cancellationToken,
        string? previouslyFetchedFrom = null,
        DateTimeOffset? previouslyFetchedUtc = null,
        int blockedReleaseCount = 0)
    {
        var wantedRows = await mediaState.ListWantedByIdsAsync(kind, [mediaId], cancellationToken);

        // A title can sit in more than one library. The one that reports a file
        // is the one worth answering about: if any library already holds it at
        // its target, another library still wanting it is not what a person is
        // asking about when they ask why it will not download.
        var wanted = wantedRows.FirstOrDefault(row => row is { HasFile: true, QualityCutoffMet: true })
                     ?? wantedRows.FirstOrDefault();

        return AcquisitionBlockerReader.Read(
            mediaId,
            kind == MediaKind.Series ? "tv" : "movies",
            title,
            wanted,
            clientHoldingRelease,
            processorHoldingFile,
            isImportExcluded,
            nextSearchSkipped: false,
            timeProvider.GetUtcNow(),
            previouslyFetchedFrom,
            previouslyFetchedUtc,
            blockedReleaseCount);
    }
}
