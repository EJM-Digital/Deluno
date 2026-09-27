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
    /// A tracked download vanished from its client entirely - not in its
    /// queue, not in its history - while it was still downloading, or after
    /// finishing with nothing found on disk to import instead (#927). No
    /// automatic search: it may have been removed on purpose, so a person
    /// chooses Search for another, Stop wanting it, or Dismiss.
    /// </summary>
    Task HandleVanishedDownloadingAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The download moved past the problem an alert described, so the recovery
    /// case written beside it no longer says anything true (#559). Only the
    /// handlers that write cases close them.
    /// </summary>
    Task HandleDispatchSettledAsync(
        string dispatchId,
        string reason,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The thing those cases were about is gone — a library, download client or
    /// processor was deleted — so they no longer say anything a person can act
    /// on (#567, #568, #569).
    ///
    /// <para>Cases are held by the two catalogues, which nothing outside them
    /// can reach; this is how a deletion asks both to settle what it named
    /// without knowing either of them.</para>
    /// </summary>
    /// <returns>How many cases closed.</returns>
    Task<int> HandleScopeSettledAsync(
        ImportRecoveryCaseScope scope,
        string reason,
        CancellationToken cancellationToken) => Task.FromResult(0);
}

public interface IDispatchRecoveryHandlerComponent : IDispatchRecoveryHandler
{
}
