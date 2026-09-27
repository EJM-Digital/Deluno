using Deluno.Contracts;

namespace Deluno.Recovery.Services;

public sealed class CompositeDispatchRecoveryHandler(IEnumerable<IDispatchRecoveryHandlerComponent> handlers)
    : IDispatchRecoveryHandler
{
    public async Task HandleGrabTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.HandleGrabTimeoutAsync(title, mediaType, downloadClientId, downloadClientName, releaseName, detailsJson, cancellationToken);
        }
    }

    public async Task HandleDetectionTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.HandleDetectionTimeoutAsync(title, mediaType, downloadClientId, downloadClientName, releaseName, entityId, detailsJson, cancellationToken);
        }
    }

    public async Task HandleImportTimeoutAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.HandleImportTimeoutAsync(title, mediaType, downloadClientId, downloadClientName, releaseName, entityId, detailsJson, cancellationToken);
        }
    }

    public async Task HandleImportFailureAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string importFailureCode,
        string importFailureMessage,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.HandleImportFailureAsync(title, mediaType, downloadClientId, downloadClientName, releaseName, entityId, importFailureCode, importFailureMessage, detailsJson, cancellationToken);
        }
    }

    public async Task HandleVanishedDownloadingAsync(
        string title,
        string mediaType,
        string downloadClientId,
        string downloadClientName,
        string releaseName,
        string entityId,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.HandleVanishedDownloadingAsync(title, mediaType, downloadClientId, downloadClientName, releaseName, entityId, detailsJson, cancellationToken);
        }
    }

    public async Task HandleDispatchSettledAsync(string dispatchId, string reason, CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.HandleDispatchSettledAsync(dispatchId, reason, cancellationToken);
        }
    }

    /// <summary>
    /// A case says a release name and never which catalogue it belongs to, so
    /// both are asked and the answer is how many closed between them.
    /// </summary>
    public async Task<int> HandleScopeSettledAsync(ImportRecoveryCaseScope scope, string reason, CancellationToken cancellationToken)
    {
        var settled = 0;
        foreach (var handler in handlers)
        {
            settled += await handler.HandleScopeSettledAsync(scope, reason, cancellationToken);
        }

        return settled;
    }
}
