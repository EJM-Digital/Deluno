namespace Deluno.Quality;

/// <summary>
/// Applies a policy set's fields to every library that has it as their
/// default policy set. Implemented in Deluno.Libraries -- this interface
/// exists so Quality's endpoints can trigger the side effect on save
/// without Quality referencing Libraries, which would create a circular
/// project reference (Libraries already references Quality).
/// </summary>
public interface IPolicySetLibraryApplier
{
    Task<int> ApplyToAssignedLibrariesAsync(string policySetId, CancellationToken cancellationToken);
}
