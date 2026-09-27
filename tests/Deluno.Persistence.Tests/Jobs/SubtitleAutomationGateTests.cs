using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #936: automatic subtitle fetching off is the new-install default, and it
/// must actually stop the job being planned -- not just hide a switch. This is
/// the pure rule the worker's plan-building loop calls; see
/// <see cref="LibraryCycleSubtitleScanTests"/> for proof that the planner
/// itself never queues a fetch once <see cref="LibraryAutomationPlanItem.WantsSubtitles"/>
/// reads false.
/// </summary>
public sealed class SubtitleAutomationGateTests
{
    [Fact]
    public void Off_means_no_automatic_fetch_even_with_languages_configured()
    {
        Assert.False(SubtitleAutomationGate.WantsSubtitles(
            subtitleFetchAutomaticEnabled: false,
            subtitleLanguages: ["en"]));
    }

    [Fact]
    public void On_with_no_languages_configured_still_fetches_nothing()
    {
        Assert.False(SubtitleAutomationGate.WantsSubtitles(
            subtitleFetchAutomaticEnabled: true,
            subtitleLanguages: []));
    }

    [Fact]
    public void On_with_a_language_configured_wants_subtitles()
    {
        Assert.True(SubtitleAutomationGate.WantsSubtitles(
            subtitleFetchAutomaticEnabled: true,
            subtitleLanguages: ["en", "fr"]));
    }

    [Fact]
    public void A_null_language_list_is_the_same_as_none()
    {
        Assert.False(SubtitleAutomationGate.WantsSubtitles(
            subtitleFetchAutomaticEnabled: true,
            subtitleLanguages: null));
    }
}
