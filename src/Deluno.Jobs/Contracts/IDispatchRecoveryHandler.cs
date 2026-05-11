namespace Deluno.Jobs.Contracts;

public interface IDispatchRecoveryHandler
{
    Task HandleGrabTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string detailsJson,
        CancellationToken cancellationToken);

    Task HandleDetectionTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken);

    Task HandleImportTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken);

    Task HandleImportFailureAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string importFailureCode,
        string importFailureMessage,
        string detailsJson,
        CancellationToken cancellationToken);
}
