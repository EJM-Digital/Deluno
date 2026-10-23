namespace Deluno.Intake;

/// <summary>Plain-words notes about import-list sources Deluno no longer supports.</summary>
public static class IntakeSourceNotes
{
    /// <summary>
    /// IMDb answers programs with a bot check (HTTP 202, empty body), so an IMDb list can never be read.
    /// The words are repeated in <c>V0139IntakeImdbListsRetired</c>, which stores them on saved sources.
    /// </summary>
    public const string ImdbNoLongerSupported =
        "IMDb lists are no longer supported because IMDb blocks programs; use MDBList or Trakt to import an IMDb list.";
}
