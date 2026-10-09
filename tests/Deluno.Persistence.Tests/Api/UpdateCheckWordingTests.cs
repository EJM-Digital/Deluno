using Deluno.Api.Backup;
using Deluno.Api.Updates;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1200: a check that could not run never says "Checked for updates.".</summary>
public sealed class UpdateCheckWordingTests
{
    private static UpdateStatusResponse Status(string state, string message)
        => new("1.0.0", "stable", "windows", "manual", true, true, true, true, false, null, state, null, false, null, null, message, null, []);

    [Fact]
    public void A_failed_check_says_it_could_not_run_in_the_feeds_own_words()
        => Assert.Equal(
            "Couldn't check for updates: the update feed isn't reachable.",
            UpdateCheckWording.Say(Status("error", "Couldn't check for updates: the update feed isn't reachable.")));

    [Fact]
    public void A_failed_check_with_no_sentence_still_says_it_could_not_run()
        => Assert.Equal("Couldn't check for updates.", UpdateCheckWording.Say(Status("error", "Update check failed.")));

    [Fact]
    public void A_check_that_ran_says_it_ran()
        => Assert.Equal("Checked for updates.", UpdateCheckWording.Say(Status("up-to-date", "You are on the latest version.")));
}
