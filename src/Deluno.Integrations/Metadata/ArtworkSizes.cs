namespace Deluno.Integrations.Metadata;

/// <summary>
/// How big the artwork Deluno caches is.
///
/// <para><b>Taken from the stylesheet, not from taste.</b> A Large poster card
/// is <c>--library-card-lg: 244px</c> and its column is <c>1fr</c>, so it draws
/// at 244&#160;CSS&#160;px <i>or wider</i> — call it 244–300. The cached poster
/// was <b>w500</b>. That is comfortable at devicePixelRatio&#160;1 and
/// <b>short at DPR&#160;2</b>, which Windows reaches at 200% display scaling and
/// every laptop retina panel reaches by default: the card wants 488–600 device
/// px and has 500. A title page's backdrop is worse — it is drawn edge to edge
/// across the content column and then <c>scale-105</c>, and #326 measured it in
/// Chrome at <b>2,335&#160;CSS&#160;px</b> against a cached <b>w1280</b>. That is
/// a 1.8× upscale at DPR&#160;1 and 3.6× at DPR&#160;2, on the largest image on
/// the page.</para>
///
/// <para><b>Why one size and not a <c>srcset</c>.</b> Artwork is not served from
/// TMDb. It is downloaded once, keyed by a hash of the remote URL, and served
/// from Deluno's own cache — so five widths would mean five downloads and five
/// cached files per title, against the same provider budget the outbound
/// throttle exists to protect. For a self-hosted app the honest answer is to
/// cache one size, make it big enough, and let the browser downscale.
/// Downscaling is sharp; upscaling is not.</para>
///
/// <para><b>What it costs, so this stays a decision rather than a slogan.</b>
/// A w780 poster is roughly 1.6× a w500 one, so a 20,000-title poster cache goes
/// from about 1.4&#160;GB to 2.2&#160;GB. Backdrops are fetched only for titles
/// somebody actually opens, so the same multiplication does not apply to them.
/// James asked for bigger posters and backdrops knowing the shape of that.</para>
///
/// <para><b>Existing titles keep their old artwork until a metadata refresh</b>,
/// because the cache key is a hash of the URL and the stored rows still hold the
/// old one. "Update all metadata" is what moves a library across, and the old
/// cached files are orphaned rather than replaced — there is no artwork cleanup
/// pass yet, which is written on #326 rather than left to be discovered.</para>
///
/// <para>Named here because the direct TMDb provider and the metadata gateway
/// both build these URLs. A size changed in one and not the other gives half a
/// library at each resolution, with nothing on screen to say why.</para>
/// </summary>
public static class ArtworkSizes
{
    /// <summary>
    /// Covers a Large card at DPR&#160;2 with room to spare, and downscales
    /// cleanly to the 126&#160;px a Small card uses.
    /// </summary>
    public const string Poster = "w780";

    /// <summary>
    /// The backdrop is drawn the full width of the page, so there is no fixed
    /// width that covers every display. TMDb offers nothing between w1280 and
    /// <c>original</c>, and <c>original</c> is the only one that is never
    /// upscaled — fetched once, for a title somebody opened.
    /// </summary>
    public const string Backdrop = "original";

    /// <summary>
    /// Cast portraits are drawn small and there are ten per title, so this stays
    /// where it was. Enlarging them would multiply the cache by the one image on
    /// the page nobody looks closely at.
    /// </summary>
    public const string Portrait = "w185";
}
