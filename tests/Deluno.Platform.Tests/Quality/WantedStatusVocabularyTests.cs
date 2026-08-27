using Deluno.Contracts;
using Deluno.Quality;

namespace Deluno.Platform.Tests.Quality;

/// <summary>
/// One meaning per value, pinned — because the ambiguity #300 describes survived
/// by being individually plausible in every place that held it.
///
/// <c>waiting</c> was set by the workflow on a title that had a file and met its
/// target, set by the migration importer when the source app reported a file,
/// described by the front end as "not searchable yet — it has not been
/// released", and rendered as *Downloading*. Four readings, one word, and no
/// test that could see the disagreement because each file was self-consistent.
/// </summary>
public sealed class WantedStatusVocabularyTests
{
    private static readonly IVersionedMediaPolicyEngine Engine = new VersionedMediaPolicyEngine();

    private static MediaWantedDecisionInput Input(
        bool hasFile,
        string? currentQuality = null,
        string? cutoff = "WEB 1080p",
        bool upgradeUntilCutoff = true,
        bool upgradeUnknownItems = false,
        bool isReleased = true)
        => new("movies", hasFile, currentQuality, cutoff, upgradeUntilCutoff, upgradeUnknownItems, isReleased);

    [Fact]
    public void Every_status_the_engine_can_return_is_one_of_the_four_words()
    {
        var inputs = new[]
        {
            Input(hasFile: false),
            Input(hasFile: false, isReleased: false),
            Input(hasFile: true, currentQuality: "WEB 720p"),
            Input(hasFile: true, currentQuality: "WEB 2160p"),
            Input(hasFile: true, currentQuality: null),
            Input(hasFile: true, currentQuality: null, upgradeUnknownItems: true),
            Input(hasFile: true, currentQuality: "WEB 720p", upgradeUntilCutoff: false),
            Input(hasFile: true, currentQuality: "WEB 720p", cutoff: null)
        };

        foreach (var input in inputs)
        {
            var status = Engine.DecideWantedState(input).WantedStatus;
            Assert.True(
                WantedStatuses.IsKnown(status),
                $"The engine returned '{status}', which is not one of: {string.Join(", ", WantedStatuses.All)}.");
        }
    }

    [Fact]
    public void A_title_that_is_here_and_at_target_is_covered_not_waiting()
    {
        // The state the server always meant by "waiting", and the front end
        // described as the opposite.
        var decision = Engine.DecideWantedState(Input(hasFile: true, currentQuality: "WEB 2160p"));

        Assert.Equal(WantedStatuses.Covered, decision.WantedStatus);
        Assert.True(decision.QualityCutoffMet);
        Assert.False(WantedStatuses.IsSearchable(decision.WantedStatus));
    }

    [Fact]
    public void A_title_that_is_not_out_yet_is_upcoming_not_missing()
    {
        // Before this existed, a film added ahead of release was stored as
        // Missing: counted against the library from the day it was added, and
        // searched for on every cycle even though nothing could be found.
        var decision = Engine.DecideWantedState(Input(hasFile: false, isReleased: false));

        Assert.Equal(WantedStatuses.Upcoming, decision.WantedStatus);
        Assert.False(WantedStatuses.IsSearchable(decision.WantedStatus));
        Assert.Contains("not out yet", decision.WantedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_title_that_is_out_and_absent_is_missing_and_gets_searched_for()
    {
        var decision = Engine.DecideWantedState(Input(hasFile: false));

        Assert.Equal(WantedStatuses.Missing, decision.WantedStatus);
        Assert.True(WantedStatuses.IsSearchable(decision.WantedStatus));
    }

    [Fact]
    public void A_title_below_its_cutoff_is_upgrade_and_stays_on_the_work_list()
    {
        var decision = Engine.DecideWantedState(Input(hasFile: true, currentQuality: "WEB 720p"));

        Assert.Equal(WantedStatuses.Upgrade, decision.WantedStatus);
        Assert.True(WantedStatuses.IsSearchable(decision.WantedStatus));
    }

    /// <summary>
    /// The two states Deluno acts on, and the two it does not. This is the whole
    /// point of the split: <c>covered</c> and <c>upcoming</c> both mean "do not
    /// search", for opposite reasons, and both used to answer to words that
    /// implied otherwise.
    /// </summary>
    [Theory]
    [InlineData("missing", true)]
    [InlineData("upgrade", true)]
    [InlineData("covered", false)]
    [InlineData("upcoming", false)]
    public void Only_missing_and_upgrade_are_searchable(string status, bool searchable)
        => Assert.Equal(searchable, WantedStatuses.IsSearchable(status));

    [Fact]
    public void The_old_word_still_reads_as_the_state_it_always_meant()
    {
        // Databases written before V0014/V0015 hold it, and so does anything
        // mid-flight across the upgrade.
        Assert.Equal(WantedStatuses.Covered, WantedStatuses.Normalize("waiting"));
    }

    [Fact]
    public void An_unrecognised_status_is_refused_rather_than_read_as_missing()
    {
        // The old normalisers mapped anything they did not know to "missing",
        // which is the most dangerous direction to guess in: it means "go and
        // download this". A typo, or a value written by a newer version and read
        // by an older one, silently became a download and nothing reported it.
        Assert.Throws<ArgumentOutOfRangeException>(() => WantedStatuses.Normalize("wanted"));
        Assert.Throws<ArgumentOutOfRangeException>(() => WantedStatuses.Normalize("downloading"));
    }

    [Fact]
    public void No_state_at_all_still_means_missing()
    {
        // A title with nothing recorded genuinely has not been found yet, which
        // is different from a word nobody recognises.
        Assert.Equal(WantedStatuses.Missing, WantedStatuses.Normalize(null));
        Assert.Equal(WantedStatuses.Missing, WantedStatuses.Normalize("   "));
    }
}
