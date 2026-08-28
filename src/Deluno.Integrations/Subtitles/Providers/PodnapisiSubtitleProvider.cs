using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Podnapisi — films and television, no account required, and better with one.
///
/// <para>The second of the two Deluno ships enabled-able out of the box. It takes
/// every wanted language in a single request, which is why it is the one to have
/// on when a library asks for more than one: eight providers × two languages is
/// sixteen round trips, and this is one of them.</para>
///
/// <para>Downloads arrive as a zip. <see cref="SubtitleArchive"/> unwraps it —
/// Weir's client did that inline and then again in a second function beside
/// it, which is the copy this port does not carry over.</para>
/// </summary>
public sealed class PodnapisiSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string ListUrl = "https://www.podnapisi.net/subtitles/search/advanced";

    public string Key => "podnapisi";

    public string DisplayName => "Podnapisi";

    public string Description => "Films and TV in a wide range of languages. Works without an account; signing in lifts the rate limit.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.Both;

    public SubtitleCredentialFields RequiredCredentials =>
        SubtitleCredentialFields.Username | SubtitleCredentialFields.Password;

    public bool CredentialsOptional => true;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var query = new List<string> { $"keywords={Uri.EscapeDataString(request.Title.Trim())}" };

        if (request.Languages.Count > 0)
        {
            // Pipe-separated, which is Podnapisi's own form for "any of these".
            var languages = string.Join('|', request.Languages
                .Select(language => Uri.EscapeDataString(language.Trim().ToLowerInvariant()))
                .Where(language => language.Length > 0));

            if (languages.Length > 0)
            {
                query.Add($"language={languages}");
            }
        }

        if (request.IsEpisode)
        {
            if (request.SeasonNumber is not null) query.Add($"seasons={request.SeasonNumber.Value}");
            if (request.EpisodeNumber is not null) query.Add($"episodes={request.EpisodeNumber.Value}");
            query.Add("movie_type=tv-series");
        }
        else
        {
            if (request.Year is not null) query.Add($"year={request.Year.Value}");
            query.Add("movie_type=movie");
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory, credentials, basicAuth: true);
        var body = await SubtitleProviderHttp.GetJsonAsync<PodnapisiSearch>(
            client, $"{ListUrl}?{string.Join('&', query)}", Key, cancellationToken);

        var found = new List<SubtitleCandidate>();
        foreach (var item in body?.Data ?? [])
        {
            var id = item.Id ?? item.SubtitleId;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var language = (item.Language ?? string.Empty).Trim().ToLowerInvariant();
            if (language.Length == 0)
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: id,
                Language: language,
                HearingImpaired: IsHearingImpaired(item),
                Forced: item.Flags?.Any(flag => string.Equals(flag, "forced", StringComparison.OrdinalIgnoreCase)) ?? false,
                ReleaseName: item.Releases?.FirstOrDefault(),
                Uploader: item.Author,
                DownloadCount: item.Downloads));
        }

        return found;
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = SubtitleProviderHttp.Create(httpClientFactory, credentials, basicAuth: true);
        var id = Uri.EscapeDataString(candidate.DownloadToken.Trim());
        return await SubtitleProviderHttp.GetBytesAsync(
            client, $"https://www.podnapisi.net/subtitles/{id}/download", Key, cancellationToken);
    }

    /// <summary>
    /// Podnapisi says "hearing impaired" in two places depending on the route it
    /// answered from, and Weir's client checked both. So does this.
    /// </summary>
    private static bool IsHearingImpaired(PodnapisiSubtitle item)
        => item.HearingImpaired == true
           || (item.Flags?.Any(flag =>
               flag.Replace('_', ' ').Equals("hearing impaired", StringComparison.OrdinalIgnoreCase)) ?? false);

    private sealed record PodnapisiSearch(
        [property: JsonPropertyName("data")] IReadOnlyList<PodnapisiSubtitle>? Data);

    private sealed record PodnapisiSubtitle(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("subtitle_id")] string? SubtitleId,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("hearing_impaired")] bool? HearingImpaired,
        [property: JsonPropertyName("flags")] IReadOnlyList<string>? Flags,
        [property: JsonPropertyName("releases")] IReadOnlyList<string>? Releases,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("downloads")] int? Downloads);
}
