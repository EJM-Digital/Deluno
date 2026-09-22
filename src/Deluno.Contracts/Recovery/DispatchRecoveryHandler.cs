namespace Deluno.Contracts;

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

    /// <summary>
    /// The download moved past the problem an alert described, so the recovery
    /// case written beside it no longer says anything true (#559). Only the
    /// handlers that write cases close them.
    /// </summary>
    Task HandleDispatchSettledAsync(
        string dispatchId,
        string reason,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IDispatchRecoveryHandlerComponent : IDispatchRecoveryHandler
{
}
