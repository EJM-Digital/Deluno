namespace Deluno.Platform.Quality;

public interface IQualityModelService
{
    Task<QualityModelSnapshot> GetAsync(CancellationToken cancellationToken);
    Task<QualityModelSnapshot> SaveAsync(UpdateQualityModelRequest request, CancellationToken cancellationToken);
}
