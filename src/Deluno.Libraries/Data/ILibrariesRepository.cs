using Deluno.Libraries.Contracts;

namespace Deluno.Libraries.Data;

public interface ILibrariesRepository
{
    Task<IReadOnlyList<LibraryItem>> ListLibrariesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DestinationRuleItem>> ListDestinationRulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LibraryViewItem>> ListLibraryViewsAsync(string userId, string variant, CancellationToken cancellationToken);

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

    Task<LibraryItem?> UpdateLibraryQualityProfileAsync(
        string id,
        UpdateLibraryQualityProfileRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryMediaPlanAsync(
        string id,
        UpdateLibraryMediaPlanRequest request,
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

    Task<bool> DeleteDestinationRuleAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteLibraryViewAsync(string userId, string id, CancellationToken cancellationToken);
}
