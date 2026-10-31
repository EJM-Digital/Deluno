using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Ktuvit — Hebrew films and TV, the source #837's issue named directly:
/// The owner's parity bar is that someone dropping Bazarr should not lose a
/// language they relied on, and Hebrew was one of them.
///
/// <para>Ktuvit's own WCF-style API needs an account on every call. Login
/// (<c>Services/MembershipService.svc/Login</c>) answers with a
/// <c>Login=</c> session cookie rather than a bearer token, so — unlike
/// Titlovi's token-in-the-query-string — this provider carries that cookie on
/// the same <see cref="HttpClient"/> for the rest of one search or download,
/// and never past it: stateless the same way every provider here is, just
/// with a cookie standing in for a token.</para>
///
/// <para>The subtitle listing itself
/// (<c>MovieInfo.aspx</c> for a film, <c>Services/GetModuleAjax.ashx</c> for
/// an episode) is an HTML table, not JSON — read with AngleSharp's own CSS
/// selectors rather than regex, because a table's column order is exactly
/// the kind of thing a hand-written pattern gets wrong quietly.</para>
///
/// <para>Verified against Bazarr's own <c>ktuvit.py</c> provider
/// (github.com/morpheus65535/bazarr, <c>custom_libs/subliminal_patch/providers</c>),
/// the reference this port's request and response shapes were read from.</para>
/// </summary>
public sealed class KtuvitSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string ApiBase = "https://www.ktuvit.me";

    public string Key => "ktuvit";

    public string DisplayName => "Ktuvit";

    public string Description => "Hebrew films and TV. Needs a free Ktuvit account.";

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
        if (!await SignInAsync(client, credentials, cancellationToken))
        {
            return [];
        }

        var searchBody = new
        {
            request = new
            {
                FilmName = request.Title.Trim(),
                Actors = Array.Empty<string>(),
                Studios = Array.Empty<string>(),
                Directors = Array.Empty<string>(),
                Genres = Array.Empty<string>(),
                Countries = Array.Empty<string>(),
                Languages = Array.Empty<string>(),
                Year = request.Year?.ToString() ?? string.Empty,
                Rating = Array.Empty<string>(),
                Page = 1,
                SearchType = request.IsEpisode ? "1" : "0",
                WithSubsOnly = false
            }
        };

        using var searchResponse = await client.PostAsJsonAsync(
            $"{ApiBase}/Services/ContentProvider.svc/SearchPage_search", searchBody, cancellationToken);
        if (!searchResponse.IsSuccessStatusCode)
        {
            return [];
        }

        var films = ReadWcfArray<KtuvitFilm>(await searchResponse.Content.ReadAsStringAsync(cancellationToken), "Films");
        var film = films.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.Id)
            && (request.Year is null || candidate.ReleaseYear is null || candidate.ReleaseYear == request.Year));
        if (film?.Id is null)
        {
            return [];
        }

        var listingHtml = request.IsEpisode && request.SeasonNumber is not null && request.EpisodeNumber is not null
            ? await SubtitleProviderHttp.GetTextAsync(
                client,
                $"{ApiBase}/Services/GetModuleAjax.ashx?moduleName=SubtitlesList&SeriesID={Uri.EscapeDataString(film.Id)}&Season={request.SeasonNumber.Value}&Episode={request.EpisodeNumber.Value}",
                Key,
                cancellationToken)
            : await SubtitleProviderHttp.GetTextAsync(
                client, $"{ApiBase}/MovieInfo.aspx?ID={Uri.EscapeDataString(film.Id)}", Key, cancellationToken);

        if (string.IsNullOrWhiteSpace(listingHtml))
        {
            return [];
        }

        var document = await new HtmlParser().ParseDocumentAsync(listingHtml, cancellationToken);
        var found = new List<SubtitleCandidate>();
        foreach (var row in document.QuerySelectorAll("tr"))
        {
            // A movie's table names the subtitle id on an <a data-subtitle-id>;
            // an episode listing carries the same id on a hidden
            // <input data-sub-id>. Reading both means one code path serves
            // both listings rather than branching the whole method a second
            // time on IsEpisode.
            var subtitleId = row.QuerySelector("[data-subtitle-id]")?.GetAttribute("data-subtitle-id")
                ?? row.QuerySelector("[data-sub-id]")?.GetAttribute("data-sub-id");
            if (string.IsNullOrWhiteSpace(subtitleId))
            {
                continue;
            }

            var releaseName = row.QuerySelectorAll("td").FirstOrDefault()?.TextContent.Trim();
            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: $"{film.Id}:{subtitleId}",
                // Ktuvit is a single-language source: everything it has is
                // Hebrew, the same fact its own site never bothers to repeat
                // per row.
                Language: "he",
                HearingImpaired: false,
                Forced: false,
                ReleaseName: string.IsNullOrWhiteSpace(releaseName) ? null : releaseName));
        }

        return found;
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var parts = candidate.DownloadToken.Split(':', 2);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("Ktuvit download token was not in the expected filmId:subtitleId shape.");
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        if (!await SignInAsync(client, credentials, cancellationToken))
        {
            throw new HttpRequestException("Ktuvit refused the sign-in needed to download this subtitle.");
        }

        var downloadRequestBody = new
        {
            request = new
            {
                FilmID = parts[0],
                SubtitleID = parts[1],
                FontSize = 0,
                FontColor = string.Empty,
                PredefinedLayout = -1
            }
        };

        using var identifierResponse = await client.PostAsJsonAsync(
            $"{ApiBase}/Services/ContentProvider.svc/RequestSubtitleDownload", downloadRequestBody, cancellationToken);
        identifierResponse.EnsureSuccessStatusCode();
        var identifier = ReadWcfString(await identifierResponse.Content.ReadAsStringAsync(cancellationToken), "DownloadIdentifier");
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new HttpRequestException("Ktuvit did not return a download identifier for this subtitle.");
        }

        return await SubtitleProviderHttp.GetBytesAsync(
            client, $"{ApiBase}/Services/DownloadFile.ashx?DownloadIdentifier={Uri.EscapeDataString(identifier)}", Key, cancellationToken);
    }

    /// <summary>
    /// Ktuvit's own login, answered with a <c>Login=</c> session cookie on
    /// the response rather than a body token — carried on this
    /// <see cref="HttpClient"/> for the rest of the call, and nowhere else.
    /// </summary>
    private static async Task<bool> SignInAsync(HttpClient client, SubtitleProviderCredentials credentials, CancellationToken cancellationToken)
    {
        var body = new { request = new { Email = credentials.Username!.Trim(), Password = credentials.Password!.Trim() } };
        using var response = await client.PostAsJsonAsync($"{ApiBase}/Services/MembershipService.svc/Login", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return false;
        }

        var loginCookie = cookies
            .Select(cookie => cookie.Split(';', 2)[0])
            .FirstOrDefault(cookie => cookie.StartsWith("Login=", StringComparison.OrdinalIgnoreCase));
        if (loginCookie is null)
        {
            return false;
        }

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", loginCookie);
        return true;
    }

    /// <summary>
    /// Ktuvit's WCF services wrap every answer in <c>{"d": ...}</c>, and
    /// <c>d</c> itself is sometimes the object directly and sometimes that
    /// object serialized a second time as a string — both are read here
    /// rather than assuming one shape and breaking on the other.
    /// </summary>
    private static IReadOnlyList<T> ReadWcfArray<T>(string json, string arrayProperty)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("d", out var d))
            {
                return [];
            }

            var element = d.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(d.GetString()!).RootElement
                : d;

            return element.TryGetProperty(arrayProperty, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.Deserialize<List<T>>()!
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadWcfString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("d", out var d))
            {
                return null;
            }

            var element = d.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(d.GetString()!).RootElement
                : d;

            return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record KtuvitFilm(
        [property: System.Text.Json.Serialization.JsonPropertyName("ID")] string? Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("ReleaseYear")] int? ReleaseYear);
}
