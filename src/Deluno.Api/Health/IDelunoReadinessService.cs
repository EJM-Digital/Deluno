namespace Deluno.Api.Health;

public interface IDelunoReadinessService
{
    Task<DelunoReadinessResponse> CheckAsync(CancellationToken cancellationToken);
}
