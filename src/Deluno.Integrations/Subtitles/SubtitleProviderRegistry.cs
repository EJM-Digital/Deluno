namespace Deluno.Integrations.Subtitles;

/// <summary>
/// Every subtitle source Deluno ships.
///
/// <para><b>Seven, not eight.</b> Weir's registry listed
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
/// </summary>
public sealed class SubtitleProviderRegistry(IEnumerable<ISubtitleProvider> providers) : ISubtitleProviderRegistry
{
    private static readonly string[] DefaultOrder =
    [
        "gestdown",
        "podnapisi",
        "opensubtitles",
        "subdl",
        "subsource",
        "subf2m",
        "yify"
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
