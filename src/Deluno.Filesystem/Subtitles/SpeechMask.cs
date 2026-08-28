using System.Numerics;

namespace Deluno.Filesystem.Subtitles;

/// <summary>
/// Where there is talking, as one bit per hundredth of a second.
///
/// <para>This is the whole idea behind timing sync, and it is smaller than it
/// sounds. A film's audio and its subtitle file are describing the same events:
/// somebody speaks, then nobody does, then somebody does again. Reduce both to
/// the same shape — a long row of bits, on where there is speech — and the
/// question "how far out is this subtitle" becomes "how far do I have to slide
/// one row along the other before they line up". <c>ffsubsync</c>, which Bazarr
/// shells out to, works exactly this way.</para>
///
/// <para><b>Why bits, and why not an FFT.</b> An hour of film at 100 frames per
/// second is 360,000 frames, and the offsets worth trying span a minute either
/// way — 12,001 of them. Comparing two arrays of 360,000 values at every one of
/// those offsets is four billion operations, which is why the reference
/// implementation reaches for an FFT. But the values here are not numbers, they
/// are single bits: packed into 64-bit words, one offset costs 5,600 AND-and-
/// popcount pairs instead of 360,000 multiplications, and the whole search comes
/// to roughly 70 million machine instructions. That runs in well under a second
/// on the sort of box Deluno lives on, exactly, with no floating point, no
/// windowing and no library.</para>
///
/// <para>Standing check 5 — <i>was it measured</i>: <c>SubtitleSyncBenchmark</c>
/// holds the wall-clock for a feature-length correlation, because "it should be
/// fast enough" is the assertion this project keeps being wrong about.</para>
/// </summary>
public sealed class SpeechMask
{
    /// <summary>
    /// A hundred frames a second.
    ///
    /// <para>Ten milliseconds is finer than anyone can perceive a subtitle being
    /// out by — the threshold where a mismatch becomes noticeable is somewhere
    /// above a tenth of a second — so the grid never limits the answer. It is
    /// also <c>ffsubsync</c>'s, which makes the two directly comparable when one
    /// of them is wrong.</para>
    /// </summary>
    public const int FramesPerSecond = 100;

    private readonly ulong[] words;

    public SpeechMask(int frames)
    {
        Frames = frames;
        words = new ulong[(frames + 63) / 64];
    }

    /// <summary>How long the mask covers, in frames.</summary>
    public int Frames { get; }

    /// <summary>How many frames are marked as speech.</summary>
    public int Population
    {
        get
        {
            var total = 0;
            foreach (var word in words)
            {
                total += BitOperations.PopCount(word);
            }

            return total;
        }
    }

    public static int ToFrames(TimeSpan value) => (int)Math.Round(value.TotalSeconds * FramesPerSecond);

    public static TimeSpan ToTime(int frames) => TimeSpan.FromSeconds((double)frames / FramesPerSecond);

    /// <summary>
    /// Marks a stretch of speech. Ranges may overlap and arrive in any order.
    /// </summary>
    public void Mark(TimeSpan start, TimeSpan end)
    {
        var from = Math.Max(0, ToFrames(start));
        var to = Math.Min(Frames, ToFrames(end));

        for (var frame = from; frame < to; frame++)
        {
            words[frame >> 6] |= 1UL << (frame & 63);
        }
    }

    /// <summary>
    /// Takes a stretch back out.
    ///
    /// <para>Here rather than in the caller because FFmpeg reports silence and
    /// this type stores speech, so somebody has to invert one into the other —
    /// and the only alternative was a loop over raw frame indices written where
    /// nothing else knows how a frame is packed.</para>
    /// </summary>
    public void Unmark(TimeSpan start, TimeSpan end)
    {
        var from = Math.Max(0, ToFrames(start));
        var to = Math.Min(Frames, ToFrames(end));

        for (var frame = from; frame < to; frame++)
        {
            words[frame >> 6] &= ~(1UL << (frame & 63));
        }
    }

    public bool this[int frame]
        => frame >= 0 && frame < Frames && (words[frame >> 6] & (1UL << (frame & 63))) != 0;

    /// <summary>
    /// Slides <paramref name="other"/> along this mask and reports where it fits
    /// best.
    ///
    /// <para>A positive shift means the subtitle has to move <i>later</i> to
    /// match the audio. The score is the count of frames where both masks are on
    /// at that shift — the raw overlap, not a ratio, because the caller needs to
    /// compare it against the overlap at zero to decide whether moving is an
    /// improvement or a coincidence.</para>
    /// </summary>
    public SpeechAlignment Correlate(SpeechMask other, int maxShiftFrames)
    {
        var atZero = Overlap(other, 0);
        var bestShift = 0;
        var bestScore = atZero;
        var total = 0L;
        var totalSquared = 0d;

        for (var shift = -maxShiftFrames; shift <= maxShiftFrames; shift++)
        {
            var score = shift == 0 ? atZero : Overlap(other, shift);
            total += score;
            totalSquared += (double)score * score;

            if (score > bestScore)
            {
                bestShift = shift;
                bestScore = score;
            }
        }

        // Both moments are accumulated on the way past. They are what separate a
        // real alignment from a lucky one, and working either out afterwards
        // would mean running the whole search twice.
        var tried = (2L * maxShiftFrames) + 1;
        var mean = (double)total / tried;
        var variance = Math.Max(0d, (totalSquared / tried) - (mean * mean));

        return new SpeechAlignment(bestShift, bestScore, atZero, mean, Math.Sqrt(variance));
    }

    /// <summary>
    /// How many frames both masks agree are speech, with <paramref name="other"/>
    /// moved <paramref name="shift"/> frames later.
    ///
    /// <para>The shift is decomposed into whole words and a remainder so the
    /// inner loop stays a straight walk over two arrays. Only the remainder costs
    /// anything: it is one funnel shift per word, which is a single instruction
    /// on every processor Deluno runs on.</para>
    /// </summary>
    private int Overlap(SpeechMask other, int shift)
    {
        var wordShift = shift >> 6;
        var bitShift = shift & 63;
        var total = 0;

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word == 0)
            {
                continue;
            }

            // The bits of `other` that land on this word once moved: the tail of
            // the word below and the head of the word at the offset.
            var low = other.WordAt(i - wordShift);
            var value = bitShift == 0
                ? low
                : (low << bitShift) | (other.WordAt(i - wordShift - 1) >> (64 - bitShift));

            total += BitOperations.PopCount(word & value);
        }

        return total;
    }

    private ulong WordAt(int index) => index >= 0 && index < words.Length ? words[index] : 0UL;
}

/// <summary>
/// Where a subtitle fits its audio best, and how much better that is than
/// leaving it alone.
/// </summary>
/// <param name="ShiftFrames">
/// Positive means the subtitle must move later. In hundredths of a second.
/// </param>
/// <param name="Score">Frames of speech the two masks share at that shift.</param>
/// <param name="ScoreAtZero">
/// Frames they shared before moving anything. Carried rather than recomputed
/// because the decision to move is a comparison between these two numbers, and a
/// caller that had to work the second one out for itself would be the second
/// place that knows how this is scored.
/// </param>
/// <param name="MeanScore">
/// The average overlap across every shift tried — what these two masks share by
/// coincidence.
/// </param>
/// <param name="ScoreDeviation">
/// How much the overlap varies from shift to shift, which is the scale a peak
/// has to be measured against.
/// </param>
public readonly record struct SpeechAlignment(int ShiftFrames, int Score, int ScoreAtZero, double MeanScore, double ScoreDeviation)
{
    public TimeSpan Shift => SpeechMask.ToTime(ShiftFrames);

    /// <summary>
    /// How much better the best fit is than the current one, as a fraction of
    /// the current one. Zero when moving gains nothing.
    /// </summary>
    public double Improvement => ScoreAtZero <= 0
        ? (Score > 0 ? 1d : 0d)
        : Math.Max(0d, (double)(Score - ScoreAtZero) / ScoreAtZero);

    /// <summary>
    /// How far the best fit stands above the coincidental one.
    ///
    /// <para><b>This is the number that says whether the answer means
    /// anything</b>, and finding that out cost a test. The first guard was
    /// coverage — how much of the subtitle lands on speech at the best shift —
    /// and it does not work: two subtitles for two different films still overlap
    /// wherever both happen to have dialogue, which for anything with talking in
    /// it is most of the running time. A synthetic pair with nothing whatever in
    /// common scored 41% coverage, comfortably past the third the service was
    /// about to require of it.</para>
    ///
    /// <para>A genuine alignment does not look like a high score. It looks like a
    /// <i>spike</i>: one shift far above every neighbour, because there is
    /// exactly one place two recordings of the same film line up. Unrelated
    /// masks have no spike — their best shift is a few per cent above their
    /// average, which is what luck buys. So the test is the ratio between the
    /// two, and it is scale-free: it does not care how talkative the film is,
    /// how long it runs, or how dense the subtitle is.</para>
    /// </summary>
    public double Prominence => MeanScore <= 0 ? 0d : Score / MeanScore;

    /// <summary>
    /// How far the peak stands above the noise, in standard deviations.
    ///
    /// <para><b>The number the threshold is actually set on, and it took a
    /// second measurement to get here.</b> <see cref="Prominence"/> is the right
    /// idea and the wrong scale: run against a real film it gave 1.64 for a
    /// subtitle that genuinely matched and 1.13 for one that did not, either
    /// side of a threshold of 1.5 — a margin of 0.14, which is not a margin. The
    /// trouble is that a ratio to the mean says nothing about how much the
    /// overlap moves about from shift to shift, and that is exactly what makes a
    /// peak a peak.</para>
    ///
    /// <para>Measured against the same audio, this separates them 4.8 to 2.0 —
    /// and the matching subtitle holds 4.7 or better at every offset from
    /// 300&#160;ms to 30&#160;s, in both directions. See
    /// <c>SubtitleTimingSyncService.RequiredPeakSigma</c>, which is where the
    /// threshold and the readings live.</para>
    /// </summary>
    public double PeakSigma => ScoreDeviation <= 0 ? 0d : (Score - MeanScore) / ScoreDeviation;
}
