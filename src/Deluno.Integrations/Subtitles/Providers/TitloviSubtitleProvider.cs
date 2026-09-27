using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Titlovi — Serbian, Croatian, Bosnian, Slovenian and Macedonian, the region
/// The owner's parity bar named directly (#837): someone dropping Bazarr for these
/// languages relied on this exact source, and the other six do not cover it.
///
/// <para>Titlovi's API sits under a Kodi-branded host and needs an account
/// token on every call, gettable at
/// <c>POST https://kodi.titlovi.com/api/subtitles/gettoken</c>. This provider is
/// stateless like the rest — see <see cref="ISubtitleProvider"/>'s own remark on
/// that — so the token is fetched fresh per search rather than cached across
/// calls; one extra request per search is the honest cost of never holding a
/// credential in memory between them.</para>
///
/// <para>Verified against Bazarr's own <c>titlovi.py</c> provider
/// (github.com/morpheus65535/bazarr, <c>custom_libs/subliminal_patch/providers</c>),
/// the reference this port's request and response shapes were read from.</para>
/// </summary>
public sealed class TitloviSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string ApiBase = "https://kodi.titlovi.com/api/subtitles";

    public string Key => "titlovi";

    public string DisplayName => "Titlovi";

    public string Description => "Serbian, Croatian, Bosnian, Slovenian and Macedonian films and TV. Needs a free Titlovi account.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.Both;

    public SubtitleCredentialFields RequiredCredentials =>
        SubtitleCredentialFields.Username | SubtitleCredentialFields.Password;

    public bool CredentialsOptional => false;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
        {
            return [];
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        var token = await SignInAsync(client, credentials, cancellationToken);
        if (token is null)
        {
            return [];
        }

        var query = new List<string>
        {
            $"query={Uri.EscapeDataString(request.Title.Trim())}",
            $"token={Uri.EscapeDataString(token.Token)}",
            $"userid={Uri.EscapeDataString(token.UserId)}",
            "json=True"
        };

        if (request.Languages.Count > 0)
        {
            query.Add($"lang={Uri.EscapeDataString(string.Join('|', request.Languages.Select(language => language.Trim())))}");
        }

        if (request.IsEpisode && request.SeasonNumber is not null)
        {
            query.Add($"season={request.SeasonNumber.Value}");
        }

        var body = await SubtitleProviderHttp.GetJsonAsync<TitloviSearch>(
            client, $"{ApiBase}/search?{string.Join('&', query)}", Key, cancellationToken);

        var found = new List<SubtitleCandidate>();
        foreach (var item in body?.SubtitleResults ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Link) || string.IsNullOrWhiteSpace(item.Lang))
            {
                continue;
            }

            // Episode 0 on a TV result is Titlovi's own way of saying "whole
            // season, one archive" rather than a single episode's track — not a
            // subtitle for the specific file this search was about.
            if (request.IsEpisode && item.Episode == 0)
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: item.Link,
                Language: MapLanguage(item.Lang),
                HearingImpaired: false,
                Forced: false,
                ReleaseName: item.Release,
                Uploader: null,
                DownloadCount: item.DownloadCount));
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

    private async Task<TitloviToken?> SignInAsync(
        HttpClient client,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/gettoken"
            + $"?username={Uri.EscapeDataString(credentials.Username!.Trim())}"
            + $"&password={Uri.EscapeDataString(credentials.Password!.Trim())}"
            + "&json=True";

        using var response = await client.PostAsync(url, content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            var body = await response.Content.ReadFromJsonAsync<TitloviTokenResponse>(cancellationToken);
            if (string.IsNullOrWhiteSpace(body?.Token) || string.IsNullOrWhiteSpace(body?.UserId))
            {
                return null;
            }

            return new TitloviToken(body.Token, body.UserId);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Titlovi names languages in their own script rather than an ISO code —
    /// the same shape Podnapisi and the rest speak in already.
    /// </summary>
    private static string MapLanguage(string titloviLanguage)
        => titloviLanguage.Trim().ToLowerInvariant() switch
        {
            "srpski" => "sr",
            "hrvatski" => "hr",
            "bosanski" => "bs",
            "slovenski" => "sl",
            "makedonski" => "mk",
            var other => other
        };

    private sealed record TitloviToken(string Token, string UserId);

    private sealed record TitloviTokenResponse(
        [property: JsonPropertyName("Token")] string? Token,
        [property: JsonPropertyName("UserId")] string? UserId);

    private sealed record TitloviSearch(
        [property: JsonPropertyName("SubtitleResults")] IReadOnlyList<TitloviSubtitle>? SubtitleResults);

    private sealed record TitloviSubtitle(
        [property: JsonPropertyName("Lang")] string? Lang,
        [property: JsonPropertyName("Season")] int? Season,
        [property: JsonPropertyName("Episode")] int? Episode,
        [property: JsonPropertyName("Link")] string? Link,
        [property: JsonPropertyName("Release")] string? Release,
        [property: JsonPropertyName("DownloadCount")] int? DownloadCount);
}
