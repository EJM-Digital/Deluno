using Deluno.Libraries.Contracts;

namespace Deluno.Libraries.Data;

public interface ILibrariesRepository : ILibrarySubtitlePreferences
{
    Task<IReadOnlyList<LibraryItem>> ListLibrariesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DestinationRuleItem>> ListDestinationRulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LibraryViewItem>> ListLibraryViewsAsync(string userId, string variant, CancellationToken cancellationToken);

    /// <summary>
    /// The single-user worker view of saved filters that opted into an existing
    /// library automation cycle. This intentionally returns only actionable
    /// views, so the worker never needs to know a user identity.
    /// </summary>
    Task<IReadOnlyList<LibraryViewItem>> ListAutomatedLibraryViewsAsync(CancellationToken cancellationToken);

    Task<DestinationRuleItem> CreateDestinationRuleAsync(
        CreateDestinationRuleRequest request,
        CancellationToken cancellationToken);

    Task<LibraryViewItem> CreateLibraryViewAsync(
        string userId,
        CreateLibraryViewRequest request,
        CancellationToken cancellationToken);

    Task<DestinationRuleItem?> UpdateDestinationRuleAsync(
        string id,
        UpdateDestinationRuleRequest request,
        CancellationToken cancellationToken);

    Task<LibraryViewItem?> UpdateLibraryViewAsync(
        string userId,
        string id,
        UpdateLibraryViewRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem> CreateLibraryAsync(
        CreateLibraryRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryAutomationAsync(
        string id,
        UpdateLibraryAutomationRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryDetailsAsync(
        string id,
        UpdateLibraryDetailsRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibrarySubtitlesAsync(
        string id,
        UpdateLibrarySubtitlesRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryMediaPlanAsync(
        string id,
        UpdateLibraryMediaPlanRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gives the library another quality profile (#1169). Null when the library or the
    /// profile does not exist, or the profile is for the other kind of media. A media
    /// plan assigned to the library that carries a different profile is let go, because
    /// the library no longer follows it.
    /// </summary>
    Task<LibraryItem?> UpdateLibraryQualityProfileAsync(
        string id,
        string qualityProfileId,
        CancellationToken cancellationToken);

    Task<int> ApplyMediaPlanToAssignedLibrariesAsync(
        string policySetId,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryWorkflowAsync(
        string id,
        UpdateLibraryWorkflowRequest request,
        CancellationToken cancellationToken);

    Task<LibraryRoutingSnapshot?> GetLibraryRoutingAsync(string libraryId, CancellationToken cancellationToken);

    Task<LibraryRoutingSnapshot?> SaveLibraryRoutingAsync(
        string libraryId,
        UpdateLibraryRoutingRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteLibraryAsync(string id, CancellationToken cancellationToken);

    /// <summary>#1411: links libraries made together by "Both" under one pair id.</summary>
    Task LinkLibraryPairAsync(string pairId, IReadOnlyList<string> libraryIds, CancellationToken cancellationToken);

    /// <summary>#1411: each linked library's pair id, by library id.</summary>
    Task<IReadOnlyDictionary<string, string>> ListLibraryPairsAsync(CancellationToken cancellationToken);

    Task<bool> DeleteDestinationRuleAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteLibraryViewAsync(string userId, string id, CancellationToken cancellationToken);
}
