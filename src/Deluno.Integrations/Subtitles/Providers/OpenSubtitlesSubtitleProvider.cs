using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// OpenSubtitles.com — the largest catalogue there is, and the one that asks
/// most of you.
///
/// <para>An API key <b>and</b> an account: the key identifies the application,
/// the login identifies you and is what the daily download allowance is counted
/// against. Both are required, and the screen says so rather than letting
/// somebody save a key and wonder why nothing downloads.</para>
///
/// <para><b>Downloads are a two-step.</b> <c>/download</c> does not return the
/// subtitle; it returns a link and spends one of your daily allowance. So the
/// token carried on a candidate is the <i>file id</i>, and the link is fetched
/// at the moment of download — which is also why a candidate that is never
/// chosen costs nothing.</para>
///
/// <para>Its 429 is meaningful and frequent, which is what
/// <see cref="SubtitleProviderRateLimitedException"/> exists for: the free tier
/// is a small number of downloads a day, and the honest response is to stop
/// asking rather than to mark the source unhealthy.</para>
/// </summary>
public sealed class OpenSubtitlesSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1";

    public string Key => "opensubtitles";

    public string DisplayName => "OpenSubtitles.com";

    public string Description => "The biggest catalogue, films and TV. Needs a free account and an API key; the free tier allows a limited number of downloads a day.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.Both;

    public SubtitleCredentialFields RequiredCredentials =>
        SubtitleCredentialFields.Username | SubtitleCredentialFields.Password | SubtitleCredentialFields.ApiKey;

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

        using var client = SubtitleProviderHttp.Create(httpClientFactory, credentials, apiKeyHeader: "Api-Key");

        var query = new List<string> { $"query={Uri.EscapeDataString(request.Title.Trim())}" };
        if (request.Languages.Count > 0)
        {
            query.Add($"languages={Uri.EscapeDataString(string.Join(',', request.Languages))}");
        }

        if (request.IsEpisode)
        {
            if (request.SeasonNumber is not null) query.Add($"season_number={request.SeasonNumber.Value}");
            if (request.EpisodeNumber is not null) query.Add($"episode_number={request.EpisodeNumber.Value}");
        }
        else if (request.Year is not null)
        {
            query.Add($"year={request.Year.Value}");
        }

        var body = await SubtitleProviderHttp.GetJsonAsync<OpenSubtitlesSearch>(
            client, $"{BaseUrl}/subtitles?{string.Join('&', query)}", Key, cancellationToken);

        var found = new List<SubtitleCandidate>();
        foreach (var item in body?.Data ?? [])
        {
            var attributes = item.Attributes;
            var fileId = attributes?.Files?.FirstOrDefault(file => file.FileId is not null)?.FileId;
            if (attributes is null || fileId is null)
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: fileId.Value.ToString(),
                Language: (attributes.Language ?? string.Empty).Trim().ToLowerInvariant(),
                HearingImpaired: attributes.HearingImpaired ?? false,
                Forced: attributes.ForeignPartsOnly ?? false,
                ReleaseName: attributes.Release,
                Uploader: attributes.Uploader?.Name,
                DownloadCount: attributes.DownloadCount));
        }

        return found;
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new InvalidOperationException("OpenSubtitles needs an API key before it will hand anything over.");
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory, credentials, apiKeyHeader: "Api-Key");

        // The login token is what the download allowance is counted against, so
        // it is fetched per download rather than cached: a stale token spends
        // nothing and fails, and this call is already the expensive half.
        var token = await LoginAsync(client, credentials, cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/download")
        {
            Content = JsonContent.Create(new { file_id = int.Parse(candidate.DownloadToken) })
        };

        var response = await SubtitleProviderHttp.SendJsonAsync<OpenSubtitlesDownload>(client, request, Key, cancellationToken);
        var link = response?.Link;
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new InvalidOperationException(
                "OpenSubtitles accepted the request but did not return a link. That is usually the daily download allowance being spent.");
        }

        using var plain = SubtitleProviderHttp.Create(httpClientFactory);
        return await SubtitleProviderHttp.GetBytesAsync(plain, link, Key, cancellationToken);
    }

    private async Task<string?> LoginAsync(
        HttpClient client,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
        {
            return null;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/login")
        {
            Content = JsonContent.Create(new
            {
                username = credentials.Username.Trim(),
                password = credentials.Password
            })
        };

        var body = await SubtitleProviderHttp.SendJsonAsync<OpenSubtitlesLogin>(client, request, Key, cancellationToken);
        return body?.Token;
    }

    private sealed record OpenSubtitlesSearch(
        [property: JsonPropertyName("data")] IReadOnlyList<OpenSubtitlesItem>? Data);

    private sealed record OpenSubtitlesItem(
        [property: JsonPropertyName("attributes")] OpenSubtitlesAttributes? Attributes);

    private sealed record OpenSubtitlesAttributes(
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("hearing_impaired")] bool? HearingImpaired,
        [property: JsonPropertyName("foreign_parts_only")] bool? ForeignPartsOnly,
        [property: JsonPropertyName("release")] string? Release,
        [property: JsonPropertyName("download_count")] int? DownloadCount,
        [property: JsonPropertyName("uploader")] OpenSubtitlesUploader? Uploader,
        [property: JsonPropertyName("files")] IReadOnlyList<OpenSubtitlesFile>? Files);

    private sealed record OpenSubtitlesUploader(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record OpenSubtitlesFile(
        [property: JsonPropertyName("file_id")] int? FileId);

    private sealed record OpenSubtitlesLogin(
        [property: JsonPropertyName("token")] string? Token);

    private sealed record OpenSubtitlesDownload(
        [property: JsonPropertyName("link")] string? Link);
}
