namespace Deluno.Integrations.Subtitles;

/// <summary>
/// Every subtitle source Deluno ships.
///
/// <para><b>Six, from Weir's eight.</b> Two went, and each for a reason that
/// was measured rather than reasoned about.</para>
///
/// <para><b>YifySubtitles is gone</b>, and it is worth writing down what was
/// actually checked, because the obvious conclusion was wrong twice.</para>
///
/// <para>Weir talked to an undocumented <c>/api?q=</c> endpoint on
/// <c>yifysubtitles.ch</c>. That host, <c>.org</c> and <c>yts-subs.com</c> now
/// answer that path with HTML. James pointed out that <c>yifysubtitles.tv</c> is
/// alive, and it is: it has a real JSON endpoint at <c>/api/search/</c> that
/// returns films. But films are not subtitles, and the listing behind them
/// (<c>/load/movie/{id}</c>) answers with an interstitial page — marked
/// <c>noindex, nofollow</c>, titled "Finding Your Fix", containing a fake
/// progress bar and a scripted redirect to an unrelated third-party
/// domain.</para>
///
/// <para>So there is no subtitle to fetch there, only an advertising redirect
/// chain to follow. Deluno does not follow those, and a provider that could only
/// ever find nothing is worse than one that is absent: its health reads fine and
/// it makes every film search one request slower.</para>
///
/// <para><b>And OpenSubtitles was one source counted twice.</b> Weir's registry listed
/// <c>opensubtitles_org</c> and <c>opensubtitles_com</c> as separate providers,
/// with separate credential fields and separate rows on its settings screen —
/// and both keys mapped to the same handler, which posts to the <c>.com</c> API
/// and records the grab as <c>.com</c> either way. It was one provider counted
/// twice, and somebody filling in two sets of credentials for it would have got
/// the same source twice or neither. That is exactly the shape this port exists
/// to leave behind.</para>
///
/// <para>Order is the order they are tried when nothing else separates them:
/// the two that need no account first, so a new install finds something before
/// it is asked to sign up for anything. A person's own priority on the stored
/// row overrides it.</para>
///
/// <para><b>Three more, for #837.</b> Titlovi (Balkan languages), Assrt
/// (Chinese) and Supersubtitles (Hungarian TV) extend real regional coverage
/// Bazarr has and the original six did not, rather than padding the count.
/// Supersubtitles needs no account and sits with the other two anonymous
/// providers; Titlovi and Assrt both need one, so they sit after.</para>
///
/// <para><b>Two more, for the rest of #837's named list.</b> Addic7ed
/// (English-language TV, one of the largest sources for it — the coordinator
/// named it directly) needs no account: solving Addic7ed's own login
/// CAPTCHA headlessly is not something this codebase does anywhere, and an
/// account there only raises a daily download cap most single-user setups
/// never hit, not what it can find. Ktuvit (Hebrew, also named directly)
/// needs one on every call, so it sits after.</para>
///
/// <para><b>Napiprojekt is deliberately not here</b>, and it belongs beside
/// the YifySubtitles note above rather than silently missing. NapiProjekt's
/// real, reliable API is a hash lookup keyed on the exact video file's own
/// bytes (a checksum of a specific byte range) — Deluno's subtitle search
/// contract (<see cref="SubtitleSearchRequest"/>) carries a title, year,
/// season/episode and release name, never the file itself, so that lookup has
/// nothing to key on here. Its only other path in is an undocumented,
/// regex-scraped HTML catalog search whose own reference implementation could
/// not be verified end to end from this session's own read of it. Shipping a
/// provider that answers wrong, or never, for the one language it exists to
/// serve is worse than shipping none (DESIGN-002) — so it is left out, on the
/// same standard applied above, until the search contract itself carries a
/// file hash to give it something real to look up.</para>
/// </summary>
public sealed class SubtitleProviderRegistry(IEnumerable<ISubtitleProvider> providers) : ISubtitleProviderRegistry
{
    private static readonly string[] DefaultOrder =
    [
        "gestdown",
        "podnapisi",
        "supersubtitles",
        "opensubtitles",
        "subdl",
        "subsource",
        "subf2m",
        "titlovi",
        "assrt",
        "addic7ed",
        "ktuvit"
    ];

    public IReadOnlyList<ISubtitleProvider> All { get; } =
        [.. providers.OrderBy(provider =>
        {
            var index = Array.IndexOf(DefaultOrder, provider.Key);
            return index < 0 ? int.MaxValue : index;
        })];

    public ISubtitleProvider? Find(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(provider => string.Equals(provider.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
