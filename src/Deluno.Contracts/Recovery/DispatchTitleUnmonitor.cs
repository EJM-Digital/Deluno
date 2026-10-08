namespace Deluno.Contracts;

/// <summary>
/// Stops a title being wanted, for a caller outside both media engines.
///
/// <para>Movies and Series stay separate engines (docs/ARCHITECTURE.md), so a
/// shared caller — such as "I don't want this title any more" removal (#924)
/// — cannot reference either engine's repository directly. Each engine
/// registers the component that owns its own dispatch entity types
/// ("movie", or "series"/"episode"); a composite resolves the one that
/// <see cref="CanHandle"/> the dispatch's <c>EntityType</c> and calls through
/// it instead.</para>
/// </summary>
public interface IDispatchTitleUnmonitorService
{
    /// <summary>Whether this engine owns dispatches of this entity type.</summary>
    bool CanHandle(string entityType);

    /// <summary>
    /// Stops monitoring the title identified by <paramref name="entityId"/> —
    /// for an episode, only that episode, not its whole series. Returns
    /// <c>false</c> when nothing matching was found to unmonitor.
    /// </summary>
    Task<bool> UnmonitorAsync(string entityType, string entityId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the title (for an episode, that episode) holds its file now. Null when this
    /// engine cannot say, so a caller never decides on a guess.
    /// </summary>
    Task<bool?> HasFileAsync(string entityType, string entityId, CancellationToken cancellationToken)
        => Task.FromResult<bool?>(null);
}

/// <summary>Marks a per-engine implementation so a composite can gather every one registered (see <see cref="IDispatchTitleUnmonitorService"/>).</summary>
public interface IDispatchTitleUnmonitorComponent : IDispatchTitleUnmonitorService
{
}
