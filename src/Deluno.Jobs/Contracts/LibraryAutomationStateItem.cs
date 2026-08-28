namespace Deluno.Jobs.Contracts;

public sealed record LibraryAutomationStateItem(
    string LibraryId,
    string LibraryName,
    string MediaType,
    string Status,
    bool SearchRequested,
    DateTimeOffset? LastPlannedUtc,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    DateTimeOffset? NextSearchUtc,
    string? LastJobId,
    string? LastError,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? NextMissingSearchUtc = null,
    DateTimeOffset? NextUpgradeSearchUtc = null,
    /// <summary>
    /// When this library's subtitles are next due to be read and fetched.
    ///
    /// <para>Its own clock rather than a share of <see cref="NextSearchUtc"/>,
    /// because subtitle work is not a search: it is not gated by the two search
    /// switches, it never reaches an indexer, and the automation screen prints
    /// <see cref="NextSearchUtc"/> as the next time this library goes looking
    /// for releases.</para>
    /// </summary>
    DateTimeOffset? NextSubtitleSearchUtc = null);
