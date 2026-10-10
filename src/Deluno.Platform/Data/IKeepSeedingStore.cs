using Deluno.Contracts;

namespace Deluno.Platform.Data;

/// <summary>
/// Where a person's keep-seeding choices (#1240) and the moves that did not work are kept. Both are empty on a new
/// install and on every upgraded one, which is what keeps the feature off until it is asked for.
/// </summary>
public interface IKeepSeedingStore
{
    Task<KeepSeedingSettings> GetAsync(CancellationToken cancellationToken);

    /// <summary>Saves the settings as given, normalized, and returns what was kept.</summary>
    Task<KeepSeedingSettings> SaveAsync(KeepSeedingSettings settings, CancellationToken cancellationToken);

    Task<IReadOnlyList<KeepSeedingProblem>> ListProblemsAsync(CancellationToken cancellationToken);

    /// <summary>Writes one problem, replacing the one with the same key.</summary>
    Task SaveProblemAsync(KeepSeedingProblem problem, CancellationToken cancellationToken);

    /// <summary>Removes the problem for one torrent; true when there was one.</summary>
    Task<bool> ClearProblemAsync(string key, CancellationToken cancellationToken);
}
