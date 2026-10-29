using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

public sealed partial class FfprobeMediaProbeServiceTests
{
    [Fact]
    public async Task A_file_another_program_holds_open_is_unreadable_before_ffprobe_is_asked_and_readable_once_released()
    {
        // A stand-in ffprobe that would answer "succeeded" for anything: if the probe reached it, the answer would be a good file.
        FakeFfprobe(Reply);
        var film = Film();

        MediaProbeInfo held;
        using (new FileStream(film, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            held = await new FfprobeMediaProbeService().ProbeAsync(film, CancellationToken.None);
        }

        Assert.Equal("unreadable", held.Status);
        Assert.Equal(ProbeFailureWords.UnreadableMessage, held.Message);
        Assert.Empty(held.VideoStreams);
        Assert.False(string.IsNullOrWhiteSpace(held.Detail), "the operating system's own words are kept as the detail");

        var released = await new FfprobeMediaProbeService().ProbeAsync(film, CancellationToken.None);
        Assert.Equal("succeeded", released.Status);
    }
}
