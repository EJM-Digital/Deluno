using Deluno.Contracts;
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

        // The search page links to itself first (/subtitles/searchbytitle?query=...), so the title is the first link that is not that page.
        var titlePath = TitleLink().Matches(searchHtml)
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault(path => !path.StartsWith("/subtitles/searchbytitle", StringComparison.OrdinalIgnoreCase));
        if (titlePath is null)
        {
            return [];
        }

        // Subf2m lists a title's subtitles one language at a time, at /subtitles/<title>/<language>/, each as /subtitles/<title>/<language>/<id>
        // (quoted either way); the download of one is that address with /download, which answers a redirect to the archive.
        var found = new List<SubtitleCandidate>();
        foreach (var code in request.Languages.Count == 0 ? ["en"] : request.Languages)
        {
            var name = SubtitleLanguages.DisplayName(code).ToLowerInvariant().Replace(' ', '_');
            var languagePath = $"{titlePath}/{name}";
            var languageHtml = await SubtitleProviderHttp.GetTextAsync(client, $"{BaseUrl}{languagePath}", Key, cancellationToken);

            found.AddRange(SubtitleLink().Matches(languageHtml)
                .Select(match => match.Groups[1].Value)
                .Where(path => path.StartsWith(languagePath + "/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(path => new SubtitleCandidate(
                    ProviderKey: Key,
                    DownloadToken: $"{BaseUrl}{path}/download",
                    Language: code,
                    HearingImpaired: false,
                    Forced: false)));
        }

        return found;
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        return await SubtitleProviderHttp.GetBytesAsync(client, candidate.DownloadToken, Key, cancellationToken);
    }

    [GeneratedRegex("href=[\"'](/subtitles/[^\"'?]+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex TitleLink();

    [GeneratedRegex("href=[\"'](/subtitles/[^\"'/?]+/[^\"'/?]+/[0-9]+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex SubtitleLink();
}
