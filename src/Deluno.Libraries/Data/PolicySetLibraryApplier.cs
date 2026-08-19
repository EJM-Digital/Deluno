using Deluno.Quality;

namespace Deluno.Libraries.Data;

public sealed class PolicySetLibraryApplier(ILibrariesRepository repository) : IPolicySetLibraryApplier
{
    public Task<int> ApplyToAssignedLibrariesAsync(string policySetId, CancellationToken cancellationToken)
        => repository.ApplyMediaPlanToAssignedLibrariesAsync(policySetId, cancellationToken);
}
