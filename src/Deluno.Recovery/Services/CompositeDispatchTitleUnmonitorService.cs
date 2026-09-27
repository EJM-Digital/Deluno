using Deluno.Contracts;

namespace Deluno.Recovery.Services;

/// <summary>
/// Gathers the per-engine components registered for <see cref="IDispatchTitleUnmonitorComponent"/>
/// (Movies, Series) and calls through to whichever one owns the given entity
/// type — the same shape as <see cref="CompositeDispatchRecoveryHandler"/>.
/// </summary>
public sealed class CompositeDispatchTitleUnmonitorService(IEnumerable<IDispatchTitleUnmonitorComponent> components)
    : IDispatchTitleUnmonitorService
{
    public bool CanHandle(string entityType) => components.Any(component => component.CanHandle(entityType));

    public async Task<bool> UnmonitorAsync(string entityType, string entityId, CancellationToken cancellationToken)
    {
        foreach (var component in components)
        {
            if (component.CanHandle(entityType))
            {
                return await component.UnmonitorAsync(entityType, entityId, cancellationToken);
            }
        }

        return false;
    }
}
