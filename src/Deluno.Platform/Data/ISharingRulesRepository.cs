using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

/// <summary>The seeding-rule matrix (#820), evaluated in priority order.</summary>
public interface ISharingRulesRepository
{
    Task<IReadOnlyList<SharingRuleItem>> ListAsync(CancellationToken cancellationToken);

    Task<SharingRuleItem> CreateAsync(CreateSharingRuleRequest request, CancellationToken cancellationToken);

    Task<SharingRuleItem?> UpdateAsync(string id, UpdateSharingRuleRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// The Max Inactive Days guard's own memory (#820): whether a download has had
/// any speed at all since the last time it was asked, because nothing else in
/// Deluno tracks that across passes.
/// </summary>
public interface ISharingActivityTracker
{
    /// <summary>
    /// Records this pass's observation and returns how many whole days this
    /// item has been continuously inactive — 0 the moment it is seen active,
    /// or the first time it is ever observed (there is nothing to compare
    /// against yet, so it is not yet "inactive").
    /// </summary>
    Task<int> RecordAndGetInactiveDaysAsync(
        string clientId,
        string queueItemId,
        bool isActiveNow,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}
