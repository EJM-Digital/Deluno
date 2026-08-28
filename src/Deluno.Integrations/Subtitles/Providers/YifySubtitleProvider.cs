using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// YifySubtitles — films only, no account, and no official API.
///
/// <para><b>Its description says so.</b> DESIGN-002: <i>"a provider that fails
/// silently is worse than one that is absent."</i> This one talks to an endpoint
/// the site has never promised to keep, so the honest thing is to ship it, say
/// what it is, and let its health tell you the day it stops — rather than leave
/// somebody wondering why a source that "works" finds nothing.</para>
/// </summary>
public sealed class YifySubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string BaseUrl = "https://yifysubtitles.ch";

    public string Key => "yify";

    public string DisplayName => "YifySubtitles";

    public string Description => "Films only, no account. Unofficial endpoint — it works today and the site has never promised to keep it, so watch its health.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.MoviesOnly;

    public SubtitleCredentialFields RequiredCredentials => SubtitleCredentialFields.None;

    public bool CredentialsOptional => false;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (request.IsEpisode)
        {
            return [];
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        var body = await SubtitleProviderHttp.GetJsonAsync<YifySearch>(
            client, $"{BaseUrl}/api?q={Uri.EscapeDataString(request.Title.Trim())}", Key, cancellationToken);

        var items = body?.Subs ?? body?.Subtitles ?? body?.Data ?? [];
        var wanted = request.Languages
            .Select(language => language.Trim().ToLowerInvariant())
            .Where(language => language.Length >= 2)
            .Select(language => language[..2])
            .ToHashSet();

        var found = new List<SubtitleCandidate>();
        foreach (var item in items)
        {
            var language = (item.Language ?? item.Lang ?? string.Empty).Trim().ToLowerInvariant();
            var link = item.Url ?? item.Download ?? item.Link;

            if (string.IsNullOrWhiteSpace(link) || language.Length < 2)
            {
                continue;
            }

            // Filtered here rather than by the caller because this endpoint
            // answers with every language it has and the list is long.
            if (wanted.Count > 0 && !wanted.Contains(language[..2]))
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: link.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? link : $"{BaseUrl}{link}",
                Language: language,
                HearingImpaired: item.Hi ?? false,
                Forced: false,
                ReleaseName: null,
                Uploader: null,
                DownloadCount: null));
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

    private sealed record YifySearch(
        [property: JsonPropertyName("subs")] IReadOnlyList<YifySubtitle>? Subs,
        [property: JsonPropertyName("subtitles")] IReadOnlyList<YifySubtitle>? Subtitles,
        [property: JsonPropertyName("data")] IReadOnlyList<YifySubtitle>? Data);

    private sealed record YifySubtitle(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("download")] string? Download,
        [property: JsonPropertyName("link")] string? Link,
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("hi")] bool? Hi);
}
