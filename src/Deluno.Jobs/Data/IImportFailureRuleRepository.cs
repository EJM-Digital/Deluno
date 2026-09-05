using Deluno.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// The user's answers to the import failure table.
///
/// <para>Only differences are stored. Everything Deluno ships with lives in
/// <see cref="ImportFailurePolicy"/> and is read from there, so this repository
/// holds opinions rather than configuration — and a failure kind that nobody
/// has an opinion about needs no row, no migration and no default written
/// twice.</para>
/// </summary>
public interface IImportFailureRuleRepository
{
    /// <summary>
    /// The overrides, keyed by reason code. Returned whole because the import
    /// pipeline asks once per failure and the table is seventeen rows at its
    /// largest.
    /// </summary>
    Task<IReadOnlyDictionary<string, BlockDecision>> GetOverridesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every known failure kind with the answer that applies to it now, its
    /// shipped default, and whether the two differ.
    /// </summary>
    Task<IReadOnlyList<ImportFailureRule>> ListAsync(CancellationToken cancellationToken);

    Task SetAsync(string reasonCode, BlockDecision decision, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a reason back to what Deluno ships with, by forgetting the opinion
    /// rather than by writing the default down. Storing the default would make
    /// it stop being one — a later change to the shipped table would not reach
    /// anybody who had ever pressed reset.
    /// </summary>
    Task ResetAsync(string reasonCode, CancellationToken cancellationToken);
}
