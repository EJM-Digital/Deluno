using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Connections.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Subtitles;

/// <summary>What happened when Deluno went looking for one language for one file.</summary>
public sealed record SubtitleFetchOutcome(
    string Language,
    bool Found,
    string? ProviderKey,
    string? WrittenPath,
    bool HearingImpaired,
    string Reason);

/// <summary>
/// Finds one language for one file, and writes it beside the video.
///
/// <para><b>Every decision is made here, once.</b> Which providers to ask, in
/// what order, which candidate wins, whether the bytes are really a subtitle,
/// and where the file lands. A provider that picked its own favourite would be a
/// second copy of the preference rule and the seven of them would disagree —
/// which is why <see cref="ISubtitleProvider"/> is deliberately incapable of
/// deciding anything.</para>
///
/// <para><b>It stops at the first success.</b> Providers are asked in priority
/// order and the first one that yields a usable file ends the search for that
/// language. Asking the rest to compare would be a better subtitle and seven
/// times the requests, and the sources that matter most are the ones with a
/// daily allowance.</para>
/// </summary>
public interface ISubtitleFetchService
{
    Task<SubtitleFetchOutcome> FetchAsync(
        SubtitleSearchRequest request,
        string language,
        bool isEpisodeMedia,
        string videoPath,
        bool excludeHearingImpaired,
        CancellationToken cancellationToken);
}

public sealed class SubtitleFetchService(
    ISubtitleProviderRegistry registry,
    ISubtitleProviderRepository repository,
    ISubtitleFileWriter fileWriter,
    TimeProvider timeProvider,
    ILogger<SubtitleFetchService> logger)
    : ISubtitleFetchService
{
    public async Task<SubtitleFetchOutcome> FetchAsync(
        SubtitleSearchRequest request,
        string language,
        bool isEpisodeMedia,
        string videoPath,
        bool excludeHearingImpaired,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var configured = await repository.ListAsync(cancellationToken);

        var askable = configured
            .Where(item => item.IsAskable(now))
            .OrderBy(item => item.Priority)
            .Select(item => (Connection: item, Provider: registry.Find(item.ProviderKey)))
            .Where(pair => pair.Provider is not null && Serves(pair.Provider!, isEpisodeMedia))
            .ToArray();

        if (askable.Length == 0)
        {
            return new SubtitleFetchOutcome(
                language, false, null, null, false,
                configured.Count == 0
                    ? "No subtitle providers are set up yet."
                    : "No enabled provider covers this kind of title right now.");
        }

        foreach (var (connection, provider) in askable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Asked for this one language rather than for all of them. The
                // caller loops languages so that a partial success is a partial
                // success: English found and Spanish not is two honest rows,
                // where one combined attempt would have to call the whole thing
                // a failure or a success and be wrong either way.
                var candidates = await provider!.SearchAsync(
                    request with { Languages = [language] },
                    Credentials(connection),
                    cancellationToken);

                var pick = Choose(candidates, language, excludeHearingImpaired);
                if (pick is null)
                {
                    continue;
                }

                var payload = await provider.DownloadAsync(pick, Credentials(connection), cancellationToken);
                var subtitle = SubtitleArchive.Extract(payload);

                if (subtitle is null || !SubtitleArchive.LooksLikeSubtitle(subtitle))
                {
                    // A rate limit, a captcha or a "sign in to download" page,
                    // served with a 200. Writing it would turn the bar green over
                    // a file that shows a player nothing.
                    await RecordAsync(connection, "degraded",
                        $"{provider.DisplayName} returned something that is not a subtitle.", false, null, cancellationToken);
                    continue;
                }

                var written = await fileWriter.WriteAsync(videoPath, language, pick.HearingImpaired, subtitle, cancellationToken);

                await RecordAsync(connection, "healthy",
                    $"Fetched a {language} subtitle for {request.Title}.", true, null, cancellationToken);

                return new SubtitleFetchOutcome(
                    Language: language,
                    Found: true,
                    ProviderKey: provider.Key,
                    WrittenPath: written,
                    HearingImpaired: pick.HearingImpaired,
                    Reason: $"Found by {provider.DisplayName}.");
            }
            catch (SubtitleProviderRateLimitedException rateLimited)
            {
                // Working, and asked to be left alone. An hour unless it said
                // otherwise — long enough to matter and short enough that a
                // provider is not lost for a day over one busy minute.
                var until = now.Add(rateLimited.RetryAfter ?? TimeSpan.FromHours(1));
                await RecordAsync(connection, "rate-limited",
                    $"{provider!.DisplayName} is rate limiting Deluno until {until:HH:mm}.", true, until, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Subtitle provider {Provider} failed for {Title}.", provider!.Key, request.Title);
                await RecordAsync(connection, "failed", exception.Message, false, null, cancellationToken);
            }
        }

        return new SubtitleFetchOutcome(
            language, false, null, null, false,
            $"No provider had a {language} subtitle for this one.");
    }

    /// <summary>
    /// The best of what came back.
    ///
    /// <para>Right language first, then the copy most people took. Download count
    /// is a crude proxy for "this one is in time", and it is the only signal all
    /// seven providers agree on — release matching is the better rule and it
    /// arrives with the quality gate DESIGN-002 describes.</para>
    ///
    /// <para>Forced is never chosen: a forced track is four lines of Elvish, and
    /// the rest of Deluno already refuses to count it as coverage.</para>
    /// </summary>
    private static SubtitleCandidate? Choose(
        IReadOnlyList<SubtitleCandidate> candidates,
        string language,
        bool excludeHearingImpaired)
        => candidates
            .Where(candidate => !candidate.Forced)
            .Where(candidate => !excludeHearingImpaired || !candidate.HearingImpaired)
            .Where(candidate => candidate.Language.StartsWith(language[..Math.Min(2, language.Length)], StringComparison.OrdinalIgnoreCase))
            // A plain track before a hearing-impaired one where both exist.
            // Hearing impaired *is* coverage — it is watchable, and Deluno counts
            // it — but it is not what most people would pick if asked, and it is
            // the one choice here somebody would notice being made for them.
            .OrderBy(candidate => candidate.HearingImpaired ? 1 : 0)
            .ThenByDescending(candidate => candidate.DownloadCount ?? 0)
            .FirstOrDefault();

    /// <summary>
    /// Whether this provider can answer for this kind of title at all.
    ///
    /// <para>Asked before the request rather than after: Gestdown returns nothing
    /// for a film and Yify nothing for an episode, and counting those as failures
    /// would mark two working sources unhealthy on every cycle.</para>
    /// </summary>
    private static bool Serves(ISubtitleProvider provider, bool isEpisode)
        => provider.Scope switch
        {
            SubtitleProviderScope.TvOnly => isEpisode,
            SubtitleProviderScope.MoviesOnly => !isEpisode,
            _ => true
        };

    private static SubtitleProviderCredentials Credentials(SubtitleProviderConnection connection)
        => new(connection.Username, connection.Secret, connection.ApiKey);

    private Task RecordAsync(
        SubtitleProviderConnection connection,
        string status,
        string message,
        bool success,
        DateTimeOffset? rateLimitedUntil,
        CancellationToken cancellationToken)
        => repository.RecordHealthAsync(
            connection.ProviderKey, status, message, latencyMs: null, success, rateLimitedUntil, cancellationToken);
}
