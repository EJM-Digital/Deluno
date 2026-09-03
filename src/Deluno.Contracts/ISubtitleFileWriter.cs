namespace Deluno.Contracts;

/// <summary>
/// Puts a fetched subtitle beside its video.
///
/// <para>Declared here rather than beside either the code that fetches or the
/// code that writes, because those two live in modules that cannot see each
/// other — Integrations knows how to get a subtitle and Filesystem owns every
/// path Deluno touches, and neither references the other. Contracts is where
/// they meet.</para>
///
/// <para>DESIGN-002 rule 5: <i>files are written by the code that already owns
/// files</i>. A private writer inside the subtitle feature with its own idea of
/// where things go is how Bazarr ended up with path mappings as its most common
/// support problem — and Deluno cannot have that problem at all, because it
/// imported the video and knows exactly where it is.</para>
/// </summary>
public interface ISubtitleFileWriter
{
    /// <summary>
    /// Writes the subtitle and returns where it landed.
    ///
    /// <para>The name is <c>&lt;video stem&gt;.&lt;language&gt;.srt</c>, or
    /// <c>.&lt;language&gt;.sdh.srt</c> for a hearing-impaired one — which is
    /// what the scanner already reads, and has to stay that way: a file the
    /// writer produced and the scanner did not recognise would be fetched again
    /// on every cycle, for ever, and nothing would look wrong.</para>
    /// </summary>
    Task<string> WriteAsync(
        string videoPath,
        string language,
        bool hearingImpaired,
        byte[] subtitle,
        CancellationToken cancellationToken,
        bool omitLanguageCode = false);
}
