namespace Deluno.Quality;

/// <summary>
/// Whether a profile will take a release of this quality at all.
///
/// <para><b>A gate, not a preference.</b> The allowed list says which tiers may
/// be grabbed, and nothing about ranking or upgrading may override it: a
/// profile allowing up to WEB 1080p must refuse a Remux 2160p, however good
/// that release is, because "I do not have the storage for 4K" is exactly what
/// the list is for.</para>
///
/// <para><b>Its own type because two callers need the same answer.</b>
/// <c>ReleaseDecisionEngine</c> asks it of every candidate during a search, and
/// the draft-profile judgement asks it while somebody is still choosing their
/// answers. Those two had drifted the moment the second one existed — the
/// judgement panel said "Deluno would take this and stop looking" about a
/// release the search would have hard-rejected, which is the worst thing the
/// one screen that explains itself can do.</para>
///
/// <para>An empty list means the profile does not constrain tiers. It is not
/// read as "nothing is allowed".</para>
/// </summary>
public static class AllowedQualityGate
{
    public static bool Accepts(IReadOnlyList<string>? allowedQualities, string? candidateQuality)
    {
        if (allowedQualities is not { Count: > 0 })
        {
            return true;
        }

        var candidate = LibraryQualityDecider.NormalizeQuality(candidateQuality) ?? candidateQuality;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            // Nothing to gate on. A release whose quality could not be read is
            // the unknown-quality question, which the profile answers
            // separately - refusing it here would answer it twice and
            // differently.
            return true;
        }

        return allowedQualities.Any(entry => string.Equals(
            LibraryQualityDecider.NormalizeQuality(entry) ?? entry,
            candidate,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Why it was refused, in the words both callers show. One sentence, in one
    /// place, so a search's Activity row and a step's live judgement cannot
    /// explain the same refusal two ways.
    /// </summary>
    public static string Refusal(IReadOnlyList<string> allowedQualities, string candidateQuality)
        => $"{candidateQuality} is not one of the qualities this profile allows ({string.Join(", ", allowedQualities)}).";
}
