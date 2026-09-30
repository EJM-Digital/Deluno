namespace Deluno.Infrastructure.Resilience;

/// <summary>
/// Lets settings code (which cannot see the metadata provider) say "start the
/// metadata service from a clean slate": forget its last failure and close its
/// circuits. Saving the metadata settings does this, because a person who has
/// just corrected a URL or key should not wait out a pause earned by the old
/// one (#1090).
/// </summary>
public interface IMetadataServiceReset
{
    Task ResetAsync(CancellationToken cancellationToken);
}
