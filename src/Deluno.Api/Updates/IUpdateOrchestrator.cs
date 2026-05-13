namespace Deluno.Api.Updates;

public interface IUpdateOrchestrator
{
    Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken);
    Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken);
    Task<UpdateStatusResponse> PrepareApplyOnNextRestartAsync(CancellationToken cancellationToken);
    Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken);
    Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken);
    Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken);
}
