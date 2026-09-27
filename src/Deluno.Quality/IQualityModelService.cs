namespace Deluno.Quality;

public interface IQualityModelService
{
    Task<QualityModelSnapshot> GetAsync(CancellationToken cancellationToken);
    Task<QualityModelSnapshot> SaveAsync(UpdateQualityModelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Restores every tier (Min/Preferred/Max) to Deluno's shipped defaults
    /// (#771), for a "Reset to defaults" action after hand-edited bands have
    /// gone wrong with no safe way back.
    /// </summary>
    Task<QualityModelSnapshot> ResetToDefaultsAsync(CancellationToken cancellationToken);
}
