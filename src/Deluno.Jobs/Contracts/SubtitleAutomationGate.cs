namespace Deluno.Jobs.Contracts;

/// <summary>
/// Whether a library's subtitle search should ever be planned (#936).
///
/// <para>Pulled out of the worker's plan-building loop so the rule -- the
/// global switch decides <i>whether</i> subtitles are fetched at all, and the
/// library's own language list still decides <i>which</i> languages -- has
/// one place to be tested from, rather than only being provable by reading
/// the worker's source.</para>
/// </summary>
public static class SubtitleAutomationGate
{
    public static bool WantsSubtitles(bool subtitleFetchAutomaticEnabled, IReadOnlyCollection<string>? subtitleLanguages)
        => subtitleFetchAutomaticEnabled && subtitleLanguages is { Count: > 0 };
}
