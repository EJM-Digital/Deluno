using System.Text.RegularExpressions;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Subf2m — films and television, no account, and no API at all.
///
/// <para><b>This one reads HTML.</b> There is no endpoint; Weir scraped the
/// search page and then the title page, and so does this. It is the most fragile
/// of the eight by a distance, and its description says so on the screen where
/// somebody chooses whether to turn it on.</para>
///
/// <para>Kept because it is genuinely good for languages the API providers are
/// thin on, and because the alternative — quietly dropping a source Weir
/// shipped — would be a regression somebody notices a month later with no way to
/// find out why.</para>
/// </summary>
public sealed partial class Subf2mSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string BaseUrl = "https://subf2m.co";

    public string Key => "subf2m";

    public string DisplayName => "Subf2m";

    public string Description => "Films and TV, no account, and unusually good on languages the API sources are thin on. It has no API — Deluno reads its pages — so it is the first to break when the site changes.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.Both;

    public SubtitleCredentialFields RequiredCredentials => SubtitleCredentialFields.None;

    public bool CredentialsOptional => false;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = SubtitleProviderHttp.Create(httpClientFactory);

        var searchTerm = Uri.EscapeDataString(request.Title.Trim().Replace(' ', '-'));
        var searchHtml = await SubtitleProviderHttp.GetTextAsync(
            client, $"{BaseUrl}/subtitles/searchbytitle?query={searchTerm}&l=", Key, cancellationToken);

        var titlePath = TitleLink().Match(searchHtml);
        if (!titlePath.Success)
        {
            return [];
        }

        var titleHtml = await SubtitleProviderHttp.GetTextAsync(
            client, $"{BaseUrl}{titlePath.Groups[1].Value}", Key, cancellationToken);

        // The language it found is whatever the page was for, and the page does
        // not say per link. The first wanted language is the honest label —
        // and the fetch records what actually landed, because a wrong label on a
        // held subtitle is worse than no subtitle.
        var language = request.Languages.FirstOrDefault() ?? "en";

        return SubtitleLink().Matches(titleHtml)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(path => new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: $"{BaseUrl}{path}",
                Language: language,
                HearingImpaired: false,
                Forced: false))
            .ToArray();
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        return await SubtitleProviderHttp.GetBytesAsync(client, candidate.DownloadToken, Key, cancellationToken);
    }

    [GeneratedRegex("href=\"(/subtitles/[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex TitleLink();

    [GeneratedRegex("href=\"(/subtitle/[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SubtitleLink();
}
