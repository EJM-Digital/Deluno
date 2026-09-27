using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Assrt (assrt.net) — Chinese subtitles, and one of the multi-language sites
/// behind Bazarr's 50+ that Deluno's original six left out entirely (#837).
///
/// <para>A free API token from assrt.net is passed on every call; there is no
/// login step. Search returns a list of matches, each needing its own detail
/// call to learn the actual file (or files, for a season pack) and their
/// direct URLs — two round trips per candidate, which is Assrt's own shape
/// rather than an extra step this port invented.</para>
///
/// <para>Verified against Bazarr's own <c>assrt.py</c> provider
/// (github.com/morpheus65535/bazarr, <c>custom_libs/subliminal_patch/providers</c>).</para>
/// </summary>
public sealed class AssrtSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string ApiBase = "https://api.assrt.net/v1";

    public string Key => "assrt";

    public string DisplayName => "Assrt";

    public string Description => "Chinese-language films and TV. Needs a free API token from assrt.net.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.Both;

    public SubtitleCredentialFields RequiredCredentials => SubtitleCredentialFields.ApiKey;

    public bool CredentialsOptional => false;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return [];
        }

        var token = credentials.ApiKey.Trim();
        using var client = SubtitleProviderHttp.Create(httpClientFactory);

        var searchUrl = $"{ApiBase}/sub/search"
            + $"?token={Uri.EscapeDataString(token)}"
            + $"&q={Uri.EscapeDataString(request.Query)}"
            + "&is_file=1";

        var searchBody = await SubtitleProviderHttp.GetJsonAsync<AssrtSearchResponse>(
            client, searchUrl, Key, cancellationToken);

        var matches = searchBody?.Sub?.Subs;
        if (matches is not { Count: > 0 })
        {
            return [];
        }

        // Assrt only wants the languages it actually carries on the matched
        // subtitle, and that lives on the *detail* call, not the search
        // result — so each candidate below is one detail lookup, matching the
        // reference client's own two-step shape rather than guessing from the
        // search summary alone.
        var found = new List<SubtitleCandidate>();
        foreach (var match in matches)
        {
            if (match.Id is null)
            {
                continue;
            }

            var detailUrl = $"{ApiBase}/sub/detail?token={Uri.EscapeDataString(token)}&id={match.Id.Value}";
            var detailBody = await SubtitleProviderHttp.GetJsonAsync<AssrtDetailResponse>(
                client, detailUrl, Key, cancellationToken);

            var detail = detailBody?.Sub?.Subs?.FirstOrDefault();
            if (detail is null)
            {
                continue;
            }

            var languages = LanguagesFor(detail);

            // A season pack answers with a file list; a single release answers
            // with one direct url. Either way what Deluno wants out of it is
            // one URL per language it was asked for.
            if (detail.FileList is { Count: > 0 })
            {
                var picked = PickFile(detail.FileList, request);
                if (picked?.Url is null)
                {
                    continue;
                }

                foreach (var language in languages)
                {
                    found.Add(Candidate(picked.Url, language, match, detail));
                }
            }
            else if (!string.IsNullOrWhiteSpace(detail.Url))
            {
                foreach (var language in languages)
                {
                    found.Add(Candidate(detail.Url, language, match, detail));
                }
            }
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

    private SubtitleCandidate Candidate(string url, string language, AssrtSub match, AssrtSubDetail detail)
        => new(
            ProviderKey: Key,
            DownloadToken: url,
            Language: language,
            HearingImpaired: false,
            Forced: false,
            ReleaseName: detail.VideoName ?? match.VideoName,
            Uploader: null,
            DownloadCount: null);

    /// <summary>
    /// A season pack's <c>filelist</c> names each entry by its own release
    /// filename — the only place an episode number survives that far down —
    /// so the file whose name carries this request's season/episode is the one
    /// wanted, not the first in the list.
    /// </summary>
    private static AssrtFile? PickFile(IReadOnlyList<AssrtFile> files, SubtitleSearchRequest request)
    {
        if (!request.IsEpisode || request.SeasonNumber is null || request.EpisodeNumber is null)
        {
            return files.Count == 1 ? files[0] : null;
        }

        var seasonEpisode = $"s{request.SeasonNumber.Value:00}e{request.EpisodeNumber.Value:00}";
        return files.FirstOrDefault(file =>
            (file.FileName ?? string.Empty).Contains(seasonEpisode, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Assrt lists every language a subtitle carries by name rather than code —
    /// <c>lang_chs</c> (Simplified Chinese), <c>lang_cht</c> (Traditional) and
    /// <c>lang_eng</c> (English) are non-null when present.
    /// </summary>
    private static IReadOnlyList<string> LanguagesFor(AssrtSubDetail detail)
    {
        var list = detail.Lang?.LangList;
        if (list is null)
        {
            return [];
        }

        var found = new List<string>();
        if (!string.IsNullOrWhiteSpace(list.LangChs)) found.Add("zh-Hans");
        if (!string.IsNullOrWhiteSpace(list.LangCht)) found.Add("zh-Hant");
        if (!string.IsNullOrWhiteSpace(list.LangEng)) found.Add("en");
        return found;
    }

    private sealed record AssrtSearchResponse(
        [property: JsonPropertyName("sub")] AssrtSearchSub? Sub);

    private sealed record AssrtSearchSub(
        [property: JsonPropertyName("subs")] IReadOnlyList<AssrtSub>? Subs);

    private sealed record AssrtSub(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("videoname")] string? VideoName);

    private sealed record AssrtDetailResponse(
        [property: JsonPropertyName("sub")] AssrtDetailSub? Sub);

    private sealed record AssrtDetailSub(
        [property: JsonPropertyName("subs")] IReadOnlyList<AssrtSubDetail>? Subs);

    private sealed record AssrtSubDetail(
        [property: JsonPropertyName("videoname")] string? VideoName,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("filelist")] IReadOnlyList<AssrtFile>? FileList,
        [property: JsonPropertyName("lang")] AssrtLang? Lang);

    private sealed record AssrtFile(
        [property: JsonPropertyName("f")] string? FileName,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record AssrtLang(
        [property: JsonPropertyName("langlist")] AssrtLangList? LangList);

    private sealed record AssrtLangList(
        [property: JsonPropertyName("lang_chs")] string? LangChs,
        [property: JsonPropertyName("lang_cht")] string? LangCht,
        [property: JsonPropertyName("lang_eng")] string? LangEng);
}
