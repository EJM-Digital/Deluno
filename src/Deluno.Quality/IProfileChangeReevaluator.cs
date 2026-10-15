namespace Deluno.Quality;

/// <summary>
/// Re-decides what the libraries own after a quality profile, or the media plan
/// that assigns one, changes (#1169). Implemented in Deluno.Media, which can see
/// both catalogues; this interface lets Quality's endpoints trigger it on save
/// without Quality referencing them.
/// </summary>
public interface IProfileChangeReevaluator
{
    /// <summary>The profile was edited: re-decide every title and episode it judges.</summary>
    Task<int> ReevaluateForProfileAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>The library was given another profile: re-decide the titles that follow it.</summary>
    Task<int> ReevaluateForLibraryAsync(string libraryId, CancellationToken cancellationToken);

    /// <summary>A media plan was saved and its libraries were given its profile: re-decide those libraries.</summary>
    Task<int> ReevaluateForPolicySetAsync(string policySetId, CancellationToken cancellationToken);

    /// <summary>A profile was deleted and what named it now follows the library or the default: say so in Activity (#1334).</summary>
    Task ReportProfileDeletedAsync(Deluno.Quality.Data.QualityProfileRemoval removal, CancellationToken cancellationToken);
}
