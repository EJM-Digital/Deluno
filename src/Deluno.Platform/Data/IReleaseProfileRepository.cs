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
}
