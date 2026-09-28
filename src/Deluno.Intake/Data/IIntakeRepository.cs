using Deluno.Infrastructure.Storage;
using Deluno.Intake.Contracts;

namespace Deluno.Intake.Data;

/// <summary>
/// Intake sources, list exclusions and title origins. Carved out of
/// <c>IPlatformSettingsRepository</c> by ADR-001 Step 1; signatures unchanged.
/// </summary>
public interface IIntakeRepository
{
    Task<IReadOnlyList<IntakeSourceItem>> ListIntakeSourcesAsync(CancellationToken cancellationToken);

    Task<IntakeSourceItem?> GetIntakeSourceAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntakeListExclusionItem>> ListActiveIntakeListExclusionsAsync(string sourceId, CancellationToken cancellationToken);

    Task<IntakeListExclusionItem?> CreateIntakeListExclusionAsync(string sourceId, CreateIntakeListExclusionRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteIntakeListExclusionAsync(string sourceId, string exclusionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntakeTitleOriginItem>> ListIntakeTitleOriginsAsync(string mediaType, string entityId, CancellationToken cancellationToken);

    Task<IntakeTitleOriginItem?> RecordIntakeTitleOriginAsync(CreateIntakeTitleOriginRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// After a list's sync fetched its entries (#1046): marks every title of
    /// this list that the sync did not see again as dropped from it, and
    /// returns the ones that are now on no list at all (every list they came
    /// from has dropped them, or no longer exists). A title seen again by a
    /// later sync is no longer dropped.
    /// </summary>
    Task<IReadOnlyList<IntakeDroppedTitle>> MarkDroppedTitlesAsync(string sourceId, DateTimeOffset syncStartedUtc, CancellationToken cancellationToken);

    Task<IntakeSourceItem> CreateIntakeSourceAsync(
        CreateIntakeSourceRequest request,
        CancellationToken cancellationToken);

    Task<IntakeSourceItem?> UpdateIntakeSourceAsync(
        string id,
        UpdateIntakeSourceRequest request,
        CancellationToken cancellationToken);

    Task<IntakeSourceItem?> RecordIntakeSourceSyncResultAsync(
        string id,
        DateTimeOffset syncedUtc,
        string status,
        string? summary,
        CancellationToken cancellationToken);

    Task<bool> DeleteIntakeSourceAsync(string id, CancellationToken cancellationToken);

}
