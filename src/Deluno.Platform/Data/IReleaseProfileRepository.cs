using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IReleaseProfileRepository
{
    Task<IReadOnlyList<ReleaseProfileItem>> ListAsync(CancellationToken cancellationToken);

    Task<ReleaseProfileItem?> GetAsync(string id, CancellationToken cancellationToken);

    Task<ReleaseProfileItem> CreateAsync(
        CreateReleaseProfileRequest request,
        CancellationToken cancellationToken);

    Task<ReleaseProfileItem?> UpdateAsync(
        string id,
        UpdateReleaseProfileRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the global profile and the profiles whose tag matches one of the
    /// title's tags. Matching happens in SQL so acquisition does not load rules
    /// for unrelated tags into every search.
    /// </summary>
    Task<IReadOnlyList<ReleaseProfileItem>> ListApplicableAsync(
        IReadOnlyList<string>? tagNames,
        CancellationToken cancellationToken);

    /// <summary>#995: "combine" (every matching rule applies) or "first-match" (top matching rule only).</summary>
    Task<string> GetRuleModeAsync(CancellationToken cancellationToken);

    Task<string> SetRuleModeAsync(string? mode, CancellationToken cancellationToken);

    /// <summary>#995: puts the rules in this order, top rule first; ids left out keep their place after these.</summary>
    Task<IReadOnlyList<ReleaseProfileItem>> ReorderAsync(
        IReadOnlyList<string> orderedIds,
        CancellationToken cancellationToken);
}
