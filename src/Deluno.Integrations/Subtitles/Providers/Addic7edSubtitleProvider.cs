using AngleSharp.Html.Parser;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Addic7ed — English-language television, and one of the largest sources
/// for it anywhere; #837's parity bar named it directly.
///
/// <para><b>Anonymous only, on purpose.</b> Addic7ed's own sign-in
/// (<c>dologin.php</c>) puts a CAPTCHA in front of a scripted login, and
/// solving one headlessly is not something Deluno does anywhere else in this
/// codebase — an account only raises Addic7ed's own daily download cap
/// (which most single-user setups never hit), it does not unlock a source
/// that would otherwise answer nothing. Browsing and downloading both work
/// signed out, which is the whole of what this provider needs.</para>
///
/// <para><b>Television only.</b> Addic7ed's film coverage is thin and its own
/// page shape for a movie is different from the season/episode table this
/// reads — the same honesty <see cref="SupersubtitlesSubtitleProvider"/>
/// already applies to its own film gap (see that class's own remark).</para>
///
/// <para>This reads HTML, not an API — <c>shows.php</c> for the name-to-id
/// map and <c>ajax_loadShow.php</c> for a season's own subtitle table — so it
/// is the second of these providers, after Subf2m, to say plainly on the
/// screen that it is the first to break when the site changes its markup.</para>
///
/// <para>Verified against Bazarr's own <c>addic7ed.py</c> provider
/// (github.com/morpheus65535/bazarr, <c>custom_libs/subliminal_patch/providers</c>),
/// the reference this port's selectors were read from.</para>
/// </summary>
public sealed class Addic7edSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string BaseUrl = "https://www.addic7ed.com";

    public string Key => "addic7ed";

    public string DisplayName => "Addic7ed";

    public string Description => "English-language TV, one of the largest sources for it. No account needed — Deluno reads its pages, so it is the first to break when the site changes. Television only.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.TvOnly;

    public SubtitleCredentialFields RequiredCredentials => SubtitleCredentialFields.None;

    public bool CredentialsOptional => true;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (!request.IsEpisode || request.SeasonNumber is null || request.EpisodeNumber is null)
        {
            return [];
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory);

        // Step one: shows.php's own name-to-id map — the only way to reach a
        // show's season page without already knowing Addic7ed's internal id.
        var showsHtml = await SubtitleProviderHttp.GetTextAsync(client, $"{BaseUrl}/shows.php", Key, cancellationToken);
        if (string.IsNullOrWhiteSpace(showsHtml))
        {
            return [];
        }

        var showsDocument = await new HtmlParser().ParseDocumentAsync(showsHtml, cancellationToken);
        var showId = showsDocument.QuerySelectorAll("td > h3 > a[href^='/show/']")
            .Select(link => (Name: link.TextContent.Trim(), Id: link.GetAttribute("href")?[6..]))
            .FirstOrDefault(candidate => string.Equals(candidate.Name, request.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            .Id;

        // No exact match: the nearest name, rather than nothing, the same
        // tolerance Supersubtitles' own autocomplete match already applies.
        showId ??= showsDocument.QuerySelectorAll("td > h3 > a[href^='/show/']")
            .Select(link => (Name: link.TextContent.Trim(), Id: link.GetAttribute("href")?[6..]))
            .FirstOrDefault(candidate => candidate.Name.Contains(request.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            .Id;

        if (string.IsNullOrWhiteSpace(showId))
        {
            return [];
        }

        var seasonHtml = await SubtitleProviderHttp.GetTextAsync(
            client, $"{BaseUrl}/ajax_loadShow.php?show={Uri.EscapeDataString(showId)}&season={request.SeasonNumber.Value}&langs=&hd=undefined&hi=undefined",
            Key, cancellationToken);
        if (string.IsNullOrWhiteSpace(seasonHtml))
        {
            return [];
        }

        var seasonDocument = await new HtmlParser().ParseDocumentAsync(seasonHtml, cancellationToken);
        var found = new List<SubtitleCandidate>();
        foreach (var row in seasonDocument.QuerySelectorAll("tr.epeven"))
        {
            var cells = row.QuerySelectorAll("td").ToArray();
            if (cells.Length < 10)
            {
                continue;
            }

            if (!int.TryParse(cells[1].TextContent.Trim(), out var episode) || episode != request.EpisodeNumber.Value)
            {
                continue;
            }

            var link = cells[9].QuerySelector("a")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: $"{BaseUrl}{(link.StartsWith('/') ? link : $"/{link}")}",
                Language: MapLanguage(cells[3].TextContent.Trim()),
                HearingImpaired: false,
                Forced: false,
                ReleaseName: cells[4].TextContent.Trim()));
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

    /// <summary>Addic7ed names languages in full English words rather than ISO codes.</summary>
    private static string MapLanguage(string addic7edLanguage)
        => addic7edLanguage.Trim().ToLowerInvariant() switch
        {
            "english" => "en",
            "spanish (spain)" or "spanish" => "es",
            "french" => "fr",
            "portuguese" => "pt",
            "portuguese (brazilian)" => "pt-BR",
            "italian" => "it",
            "german" => "de",
            "dutch" => "nl",
            "polish" => "pl",
            "greek" => "el",
            var other => other
        };
}
