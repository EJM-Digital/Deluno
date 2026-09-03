using System.Diagnostics;
using System.Text;
using Deluno.Filesystem.Subtitles;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// Timing sync's two halves, tested where they can be: reading a subtitle, and
/// finding how far one of them is out.
///
/// <para>What these deliberately do <b>not</b> test is whether the answer is
/// right on a real film — that needs a real film, real audio and a real FFmpeg,
/// and DESIGN-002's whole record of this project says a green suite has never
/// been the thing that found the defect. The rig does that half. These hold the
/// arithmetic still so that when the rig disagrees, it is not the arithmetic.</para>
/// </summary>
public sealed class SubtitleTimingSyncTests
{
    [Fact]
    public void A_subtitle_is_read_as_the_times_it_asserts()
    {
        var cues = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes(
            "1\r\n00:00:01,000 --> 00:00:03,500\r\nHello.\r\n\r\n" +
            "2\r\n00:01:02,250 --> 00:01:04,000\r\nTwo lines\r\nof dialogue.\r\n\r\n"));

        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), cues[0].End);
        Assert.Equal("Hello.", cues[0].Text);

        Assert.Equal(new TimeSpan(0, 0, 1, 2, 250), cues[1].Start);
        Assert.Equal("Two lines\nof dialogue.", cues[1].Text);
    }

    /// <summary>
    /// Every one of these is a real thing subtitles in the wild do. A parser
    /// that refused any of them would leave the file untouched and say "sync did
    /// nothing", which is the least useful possible failure.
    /// </summary>
    [Theory]
    // Unix line endings.
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nA\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n")]
    // A full stop for the fractional separator.
    [InlineData("1\r\n00:00:01.000 --> 00:00:02.000\r\nA\r\n\r\n2\r\n00:00:03.000 --> 00:00:04.000\r\nB\r\n")]
    // No blank line between the cues.
    [InlineData("1\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nB\r\n")]
    // Position data after the end stamp.
    [InlineData("1\r\n00:00:01,000 --> 00:00:02,000  X1:100 X2:600\r\nA\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nB\r\n")]
    // No index numbers at all.
    [InlineData("00:00:01,000 --> 00:00:02,000\r\nA\r\n\r\n00:00:03,000 --> 00:00:04,000\r\nB\r\n")]
    // No hour field.
    [InlineData("1\r\n00:01,000 --> 00:02,000\r\nA\r\n\r\n2\r\n00:03,000 --> 00:04,000\r\nB\r\n")]
    public void The_shapes_real_files_arrive_in_are_all_read(string content)
    {
        var cues = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes(content));

        Assert.Equal(2, cues.Count);
        Assert.Equal("A", cues[0].Text);
        Assert.Equal("B", cues[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(4), cues[1].End);
    }

    [Fact]
    public void Bytes_that_are_not_a_subtitle_produce_no_cues_rather_than_an_exception()
    {
        // What a provider serves instead of a file when it wants you to sign in.
        var page = Encoding.UTF8.GetBytes("<html><body>Please log in to download.</body></html>");

        Assert.Empty(SubtitleTimeline.Parse(page));
        Assert.Empty(SubtitleTimeline.Parse([]));
    }

    [Fact]
    public void A_subtitle_that_is_not_utf8_keeps_its_accents()
    {
        // Windows-1252 0x92 is a right single quote, and 0xE9 is é. Read as
        // UTF-8 these are invalid bytes; read as Latin-1 the first is a control
        // code. Either way somebody reads a mangled film.
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nIt"));
        bytes.Add(0x92);
        bytes.AddRange(Encoding.ASCII.GetBytes("s caf"));
        bytes.Add(0xE9);
        bytes.AddRange(Encoding.ASCII.GetBytes(".\r\n\r\n"));

        var cues = SubtitleTimeline.Parse([.. bytes]);

        Assert.Single(cues);
        Assert.Equal("It’s café.", cues[0].Text);
    }

    /// <summary>
    /// A cue shoved before the start of the film keeps its length.
    ///
    /// <para>The first version clamped the start and the end independently,
    /// which turned every cue in the opening seconds into
    /// <c>00:00:00 --&gt; 00:00:00</c>, a cue no player shows. That is dropping
    /// the line while the comment above it claimed not to.</para>
    /// </summary>
    [Fact]
    public void Shifting_renumbers_from_one_and_keeps_a_clamped_cue_on_screen()
    {
        var cues = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes(
            "7\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\n\r\n9\r\n00:00:10,000 --> 00:00:11,000\r\nB\r\n"));

        var shifted = Encoding.UTF8.GetString(SubtitleTimeline.Shift(cues, TimeSpan.FromSeconds(-3)));

        // One second long before the shift, one second long after it.
        Assert.Contains("1\r\n00:00:00,000 --> 00:00:01,000\r\nA", shifted, StringComparison.Ordinal);
        Assert.Contains("2\r\n00:00:07,000 --> 00:00:08,000\r\nB", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shift_survives_a_round_trip_through_the_file()
    {
        var original = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes(
            "1\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\n\r\n2\r\n00:10:00,500 --> 00:10:02,750\r\nB\r\n"));

        var reread = SubtitleTimeline.Parse(SubtitleTimeline.Shift(original, TimeSpan.FromMilliseconds(2500)));

        Assert.Equal(TimeSpan.FromMilliseconds(3500), reread[0].Start);
        Assert.Equal(new TimeSpan(0, 0, 10, 3, 0), reread[1].Start);
        Assert.Equal(new TimeSpan(0, 0, 10, 5, 250), reread[1].End);
    }

    /// <summary>
    /// The one that matters: a mask correlated against a copy of itself, moved a
    /// known distance, must report that distance and no other.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(37)]      // Sub-word: inside a single 64-bit block.
    [InlineData(-37)]
    [InlineData(250)]     // 2.5 s, the everyday case.
    [InlineData(-250)]
    [InlineData(64)]      // Exactly one word, where the shift decomposition changes shape.
    [InlineData(-64)]
    [InlineData(5999)]    // A frame short of the search bound.
    [InlineData(-5999)]
    public void A_known_offset_is_recovered_exactly(int shiftFrames)
    {
        var audio = Dialogue(60_000, seed: 12);
        var subtitle = new SpeechMask(60_000);

        for (var frame = 0; frame < audio.Frames; frame++)
        {
            if (audio[frame])
            {
                // Moved the other way, because a subtitle that must move later
                // is one that currently sits earlier than the speech.
                subtitle.Mark(SpeechMask.ToTime(frame - shiftFrames), SpeechMask.ToTime(frame - shiftFrames + 1));
            }
        }

        var alignment = audio.Correlate(subtitle, SpeechMask.ToFrames(TimeSpan.FromSeconds(60)));

        Assert.Equal(shiftFrames, alignment.ShiftFrames);
    }

    /// <summary>
    /// The test that changed the design.
    ///
    /// <para>The guard was originally coverage — how much of the subtitle lands
    /// on speech at the best shift — on the reasoning that an unrelated subtitle
    /// would land on very little. This test failed, and it was right to: two
    /// films with dialogue in them are both talking most of the time, so an
    /// unrelated pair overlaps a great deal wherever both happen to speak. It is
    /// the <i>sharpness</i> of the peak, not its height, that says two
    /// recordings are of the same thing — and not its height above the average
    /// either, which was the second wrong answer: that separated a real match
    /// from a false one by 1.64 to 1.13 on real audio, which is not a
    /// separation.</para>
    /// </summary>
    [Fact]
    public void A_subtitle_for_a_different_film_finds_no_convincing_fit()
    {
        var audio = Dialogue(60_000, seed: 12);
        var matching = Dialogue(60_000, seed: 12);
        var unrelated = Dialogue(60_000, seed: 99);

        var maxShift = SpeechMask.ToFrames(TimeSpan.FromSeconds(60));
        var right = audio.Correlate(matching, maxShift);
        var wrong = audio.Correlate(unrelated, maxShift);

        // Coverage cannot tell these apart, which is the whole point.
        var wrongCoverage = (double)wrong.Score / Math.Min(audio.Population, unrelated.Population);
        Assert.True(wrongCoverage > 0.34, "The premise of this test has changed: unrelated dialogue no longer overlaps heavily.");

        // How far the peak stands above the noise can, with room to spare on
        // both sides. Three sigma is the service's threshold, measured on the
        // lab episode's real audio — see RequiredPeakSigma.
        Assert.True(wrong.PeakSigma < 3.0, $"Unrelated dialogue peaked {wrong.PeakSigma:F1} sigma above its noise, which sync would have believed.");
        Assert.True(right.PeakSigma > 3.0, $"Matching dialogue peaked only {right.PeakSigma:F1} sigma above its noise, so nothing would ever be synced.");
    }

    /// <summary>
    /// Standing check 5. A ten-minute mask against a minute of offsets is the
    /// smallest realistic unit of this work, and it runs on the subtitle sync
    /// lane beside everything else Deluno is doing.
    /// </summary>
    [Fact]
    public void A_correlation_costs_a_fraction_of_a_second()
    {
        var audio = Dialogue(60_000, seed: 3);
        var subtitle = Dialogue(60_000, seed: 3);

        var stopwatch = Stopwatch.StartNew();
        audio.Correlate(subtitle, SpeechMask.ToFrames(TimeSpan.FromSeconds(60)));
        stopwatch.Stop();

        // Measured at about 30 ms on the six-core rig. The assertion is an order
        // of magnitude above that, so it catches an algorithm that regressed to
        // per-frame comparison without failing on a busy build agent.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 1500,
            $"A ten-minute correlation took {stopwatch.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Something shaped like speech: bursts of a second or two with pauses
    /// between them, deterministic from a seed so a failure can be reproduced.
    /// </summary>
    [Fact]
    public void Rescaling_moves_both_ends_of_every_cue_by_the_same_factor()
    {
        var cues = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes(
            "1\r\n00:00:10,000 --> 00:00:12,000\r\nA\r\n\r\n" +
            "2\r\n01:00:00,000 --> 01:00:02,000\r\nB\r\n\r\n"));

        var rescaled = SubtitleTimeline.Rescale(cues, 25d / (24000d / 1001d));

        // A cue ten seconds in moves by less than half a second; the same
        // proportional error an hour in is over two and a half minutes. That
        // spread is the whole reason a shift cannot fix this.
        Assert.Equal(10.427, rescaled[0].Start.TotalSeconds, 3);
        Assert.Equal(12.512, rescaled[0].End.TotalSeconds, 3);
        Assert.Equal(3753.750, rescaled[1].Start.TotalSeconds, 3);

        // The line is on screen for the same fraction of the film it always was.
        Assert.Equal(
            (cues[1].End - cues[1].Start).TotalSeconds,
            (rescaled[1].End - rescaled[1].Start).TotalSeconds * ((24000d / 1001d) / 25d),
            3);
    }

    /// <summary>
    /// The failure the shift search on its own cannot answer.
    ///
    /// <para>A subtitle timed against the 25 fps cut, played against the 23.976
    /// fps master, is not late — it is fast, by a proportion. The single best
    /// shift lands one end of the film and abandons the other, and this is the
    /// score that says so.</para>
    ///
    /// <para>The rescaled candidate is built exactly the way the service builds
    /// it, so what is under test is the repair the service actually applies —
    /// including that <c>Rescale</c> at one ratio undoes <c>Rescale</c> at its
    /// inverse to within the mask's own hundredth of a second.</para>
    /// </summary>
    [Fact]
    public void A_framerate_mismatch_only_lines_up_once_the_timeline_is_rescaled()
    {
        var ratio = 25d / (24000d / 1001d);
        var duration = TimeSpan.FromMinutes(75);

        var authored = DialogueCues(lines: 600, seed: 41);
        // The subtitle as it actually arrives: the same words, timed against the
        // faster cut, so every line is progressively early.
        var arrived = SubtitleTimeline.Rescale(authored, ratio);

        var audio = Speech(authored, duration);
        var maxShift = SpeechMask.ToFrames(TimeSpan.FromSeconds(60));

        var shifted = audio.Correlate(Speech(arrived, duration), maxShift);
        var rescaled = audio.Correlate(Speech(SubtitleTimeline.Rescale(arrived, 1 / ratio), duration), maxShift);

        // Undoing the speed change recovers nearly all the dialogue. The best
        // single shift cannot: it lands one end of the film and leaves the
        // other progressively out, and a good part of what it does recover is
        // the coincidence of two dense masks overlapping at any offset.
        var spoken = audio.Population;
        Assert.True(
            rescaled.Score > spoken * 0.9,
            $"Rescaled overlap {rescaled.Score} should recover nearly all {spoken} spoken frames.");
        Assert.True(
            shifted.Score < spoken * 0.75,
            $"The best shift recovered {shifted.Score} of {spoken}, which is not the mismatch this test means to build.");
    }

    private static IReadOnlyList<SubtitleCue> DialogueCues(int lines, int seed)
    {
        var random = new Random(seed);
        var cues = new SubtitleCue[lines];
        var at = 5.0;
        for (var i = 0; i < lines; i++)
        {
            var length = 1.0 + (random.NextDouble() * 2);
            cues[i] = new SubtitleCue(
                TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(at + length), $"Line {i}.");
            at += length + 2 + (random.NextDouble() * 6);
        }

        return cues;
    }

    private static SpeechMask Speech(IReadOnlyList<SubtitleCue> cues, TimeSpan duration)
    {
        var mask = new SpeechMask(SpeechMask.ToFrames(duration));
        foreach (var cue in cues)
        {
            mask.Mark(cue.Start, cue.End);
        }

        return mask;
    }

    private static SpeechMask Dialogue(int frames, int seed)
    {
        var mask = new SpeechMask(frames);
        var random = new Random(seed);
        var at = 0;

        while (at < frames)
        {
            var speech = random.Next(60, 250);
            mask.Mark(SpeechMask.ToTime(at), SpeechMask.ToTime(Math.Min(frames, at + speech)));
            at += speech + random.Next(40, 400);
        }

        return mask;
    }
}
