using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public sealed class CompositeDispatchRecoveryHandler(IEnumerable<IDispatchRecoveryHandler> handlers)
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
}
