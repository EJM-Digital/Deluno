using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Moq;

namespace Deluno.Worker.Tests.Media;

/// <summary>
/// What happens when the rules judging the library change and Deluno re-decides
/// what the library owns (#1329).
/// </summary>
public sealed class WantedStateReevaluatorLowGapTests
{
    private static LibraryItem Library(string? cutoff)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "library-1", "Movies", "movies", "main", "/media/movies", null, null, "WEB 1080p", cutoff,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private static (WantedStateReevaluator Subject, Mock<IMediaStateRepository> State, List<string> Said) Subject(LibraryItem[] libraries, WantedReevaluationResult? result = null)
    {
        var librariesRepository = new Mock<ILibrariesRepository>();
        librariesRepository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
        var quality = new Mock<IQualityRepository>();
        quality.Setup(instance => instance.ListQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<QualityProfileItem>());
        var state = new Mock<IMediaStateRepository>();
        state.Setup(instance => instance.ReevaluateWantedStateAsync(
                It.IsAny<MediaKind>(), It.IsAny<WantedReevaluationScope>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? WantedReevaluationResult.None);
        var said = new List<string>();
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(instance => instance.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, _, _, _, _, _) => said.Add(message))
            .ReturnsAsync((ActivityEventItem)null!);
        return (new WantedStateReevaluator(librariesRepository.Object, quality.Object, state.Object, activity.Object), state, said);
    }

    [Fact]
    public async Task Re_evaluating_a_profile_that_does_not_exist_does_nothing_and_a_library_with_no_cutoff_is_skipped()
    {
        var (subject, state, said) = Subject([Library(cutoff: null)]);

        var unknownProfile = await subject.ReevaluateForProfileAsync("no-such-profile", CancellationToken.None);
        var noCutoff = await subject.ReevaluateLibraryAsync("library-1", CancellationToken.None);

        Assert.Equal(0, unknownProfile);
        Assert.Equal(WantedReevaluationResult.None, noCutoff);
        state.Verify(instance => instance.ReevaluateWantedStateAsync(
            It.IsAny<MediaKind>(), It.IsAny<WantedReevaluationScope>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(said);
    }

    [Fact]
    public async Task The_Activity_line_says_how_many_can_now_be_upgraded_or_how_many_now_meet_the_quality_asked_for()
    {
        var (upgrades, _, upgradeLines) = Subject([Library("WEB 2160p")], new WantedReevaluationResult(5, 0, 3, 1));
        var (covered, _, coveredLines) = Subject([Library("WEB 720p")], new WantedReevaluationResult(5, 0, 0, 2));
        var (unchanged, _, unchangedLines) = Subject([Library("WEB 1080p")], new WantedReevaluationResult(4, 0, 0, 0));
        var (nothing, _, nothingLines) = Subject([Library("WEB 1080p")]);

        await upgrades.ReevaluateLibraryAsync("library-1", CancellationToken.None);
        await covered.ReevaluateLibraryAsync("library-1", CancellationToken.None);
        await unchanged.ReevaluateLibraryAsync("library-1", CancellationToken.None);
        await nothing.ReevaluateLibraryAsync("library-1", CancellationToken.None);

        Assert.Equal("Deluno rechecked what you own because Movies now uses WEB 1080p. 3 can now be upgraded.", Assert.Single(upgradeLines));
        Assert.EndsWith(" 2 now meet the quality asked for.", Assert.Single(coveredLines));
        Assert.EndsWith("now uses WEB 1080p.", Assert.Single(unchangedLines));
        Assert.Empty(nothingLines);
    }
}
