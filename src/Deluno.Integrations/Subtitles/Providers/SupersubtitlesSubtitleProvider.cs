using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Supersubtitles (feliratok.eu) — Hungarian television, no account needed.
///
/// <para>TV only. The site's own show/episode lookup answers JSON — the shape
/// this provider reads — but its film search only exists as an HTML page
/// Bazarr scrapes with a full HTML parser. Claiming movie support here without
/// one would be a provider that reads as configured and finds nothing for half
/// of what it claims to cover, which is worse than being honest about the
/// gap (see <see cref="SubtitleProviderRegistry"/>'s own note on YifySubtitles).
/// The owner's parity bar is about not losing a source someone relied on, not about
/// promising more than this port safely reads.</para>
///
/// <para>Verified against Bazarr's own <c>supersubtitles.py</c> provider
/// (github.com/morpheus65535/bazarr, <c>custom_libs/subliminal_patch/providers</c>).</para>
/// </summary>
public sealed class SupersubtitlesSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string SiteBase = "https://www.feliratok.eu";

    public string Key => "supersubtitles";

    public string DisplayName => "Supersubtitles";

    public string Description => "Hungarian TV subtitles. No account needed. Television only — its film search has no API to read.";

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

        // Step one: the show's own numeric id, found by matching this
        // request's title against the site's autocomplete-style name search —
        // the only way in without already knowing feliratok.eu's internal id.
        var nameUrl = $"{SiteBase}/index.php?term={Uri.EscapeDataString(request.Title.Trim())}&nyelv=0&action=autoname";
        var names = await SubtitleProviderHttp.GetJsonAsync<List<SupersubtitlesShow>>(client, nameUrl, Key, cancellationToken);
        var show = names?.FirstOrDefault(candidate =>
            (candidate.Name ?? string.Empty).Contains(request.Title.Trim(), StringComparison.OrdinalIgnoreCase));

        if (show?.Id is null)
        {
            return [];
        }

        // Step two: every subtitle the site has for that show's season, so a
        // whole-season pack (ep == -1) can be told apart from a single
        // episode's own track.
        var episodesUrl = $"{SiteBase}/index.php?action=xbmc&sid={Uri.EscapeDataString(show.Id)}&ev={request.SeasonNumber.Value}&rtol={request.EpisodeNumber.Value}";
        var body = await SubtitleProviderHttp.GetTextAsync(client, episodesUrl, Key, cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        Dictionary<string, SupersubtitlesEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, SupersubtitlesEntry>>(body);
        }
        catch (JsonException)
        {
            return [];
        }

        if (entries is null)
        {
            return [];
        }

        var found = new List<SubtitleCandidate>();
        foreach (var entry in entries.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.SubtitleId) || string.IsNullOrWhiteSpace(entry.Language))
            {
                continue;
            }

            // "-1" is the site's own mark for a whole-season archive rather
            // than this one episode's track — a season pack is not a
            // subtitle for the specific file this search was about.
            if (entry.EpisodeMarker == "-1")
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: $"{SiteBase}/index.php?action=letolt&felirat={Uri.EscapeDataString(entry.SubtitleId)}",
                Language: MapLanguage(entry.Language),
                HearingImpaired: false,
                Forced: false,
                ReleaseName: entry.ReleaseName,
                Uploader: entry.Uploader,
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

    /// <summary>Feliratok names languages by nationality rather than ISO code.</summary>
    private static string MapLanguage(string siteLanguage)
        => siteLanguage.Trim().ToLowerInvariant() switch
        {
            "magyar" => "hu",
            "angol" => "en",
            var other => other
        };

    private sealed record SupersubtitlesShow(
        [property: JsonPropertyName("ID")] string? Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record SupersubtitlesEntry(
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("fnev")] string? ReleaseName,
        [property: JsonPropertyName("felirat")] string? SubtitleId,
        [property: JsonPropertyName("feltolto")] string? Uploader,
        [property: JsonPropertyName("ep")] string? EpisodeMarker);
}
