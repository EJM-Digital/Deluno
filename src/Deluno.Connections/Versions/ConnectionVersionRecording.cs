using Deluno.Contracts;

namespace Deluno.Connections.Versions;

/// <summary>Remembering what a check read, from the places that run one (#1117).</summary>
public static class ConnectionVersionRecording
{
    /// <summary>
    /// Stores the version a check read, if it read one. Safe on a missing store and on a null version, and never
    /// throws: a version that cannot be kept must not fail the check that found it.
    /// </summary>
    public static async Task RememberVersionAsync(
        this IConnectionVersionStore? store,
        string kind,
        string id,
        string? project,
        string? version,
        CancellationToken cancellationToken)
    {
        if (store is null || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        try
        {
            await store.RecordAsync(kind, id, project, version, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }
}
