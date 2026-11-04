using Deluno.Media;

namespace Deluno.Persistence.Tests.Media;

/// <summary>A file restored from the bin is linked to the episode its name gives (#1472: letters like s7e0 inside a show's name are not a code).</summary>
public sealed class RestoredFileEpisodeCodeTests
{
    [Theory]
    [InlineData("Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv", 1, 1)]
    [InlineData("Gppackmw2bs7e0.S01E02.1080p.WEB-DL.x264-GRP.mkv", 1, 2)]
    [InlineData("Lens7e1.S02E05.mkv", 2, 5)]
    [InlineData("show.s03e10.mkv", 3, 10)]
    public void A_restored_files_episode_is_the_code_in_its_name_not_letters_inside_the_show_name(string fileName, int season, int episode)
        => Assert.Equal((season, episode), TitleSnapshotService.EpisodeCodeOf(fileName));

    [Fact]
    public void A_name_with_no_episode_code_gives_none()
        => Assert.Null(TitleSnapshotService.EpisodeCodeOf("Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
}
