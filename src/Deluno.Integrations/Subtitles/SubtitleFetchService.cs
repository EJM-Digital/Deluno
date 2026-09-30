using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Connections.Data;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Subtitles;

/// <summary>What happened when Deluno went looking for one language for one file.</summary>
public sealed record SubtitleFetchOutcome(
    string Language,
    bool Found,
    string? ProviderKey,
    string? WrittenPath,
    bool HearingImpaired,
    string Reason,
    /// <summary>
    /// How well the subtitle that was written fits the file, so the caller can
    /// record it and the shelf can stop pretending. Meaningless when
    /// <c>Found</c> is false.
    /// </summary>
    SubtitleMatch Match = SubtitleMatch.AnyRelease,
    /// <summary>
    /// Typed failures from providers Deluno tried before this result. The list is
    /// retained for Activity and diagnostics; <see cref="Failure"/> is the last
    /// one only when the fetch itself did not find a usable subtitle.
    /// </summary>
    IReadOnlyList<IntegrationFailure>? Failures = null,
    /// <summary>
    /// Named, deterministic content cleanups applied before the file was
    /// written. Null means the provider bytes were written unchanged.
    /// </summary>
    IReadOnlyList<string>? AppliedModifications = null,
    /// <summary>
    /// The stable identity of the candidate that was written, so the caller can
    /// record it beside the fetched row and later blocklist that exact file
    /// rather than every candidate the provider ever offers for this language
    /// (#895, #897, #841). Null when nothing was written.
    /// </summary>
    string? SourceKey = null,
    /// <summary>
    /// Candidates this fetch rejected as not really a subtitle — a rate limit
    /// page or a captcha served with a 200 — and moved past to try the next
    /// one from the same provider, rather than giving up on the provider
    /// entirely (#898). The caller blocklists each of these so a later search
    /// never downloads the same bad file again.
    /// </summary>
    IReadOnlyList<RejectedSubtitleCandidate>? RejectedCandidates = null)
{
    public IntegrationFailure? Failure
        => Found ? null : Failures?.LastOrDefault();
}

/// <summary>One candidate a fetch downloaded and found not to be a real subtitle.</summary>
public sealed record RejectedSubtitleCandidate(
    string BlockKey,
    string Language,
    string ProviderKey,
    string? ReleaseName,
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
        // This language's hearing-impaired preference — one of
        // SubtitleHearingImpairedPreferences. A wrong-type candidate is
        // filtered out before ranking, so it is never picked and the next
        // candidate is tried instead (#836, #923).
        string hearingImpairedPreference,
        CancellationToken cancellationToken,
        SubtitleContentModificationPolicy? contentPolicy = null,
        SubtitleNamePolicy? namePolicy = null,
        bool omitLanguageCode = false,
        // Candidates already refused for this file (#897) — a bad download
        // (#898), a rejection the person made themselves (#841), or a
        // subtitle they deleted from disk (#895). Never offered again.
        IReadOnlyCollection<string>? blockedKeys = null);

    /// <summary>
    /// Finds a forced track for one language and writes it as an extra file
    /// alongside the full subtitle, when the library asked for it (#835,
    /// #923). Never counted as coverage — the caller records it with
    /// <c>Forced: true</c>, which the held-language rollup already excludes.
    /// </summary>
    Task<SubtitleFetchOutcome> FetchForcedAsync(
        SubtitleSearchRequest request,
        string language,
        bool isEpisodeMedia,
        string videoPath,
        CancellationToken cancellationToken,
        SubtitleContentModificationPolicy? contentPolicy = null,
        SubtitleNamePolicy? namePolicy = null,
        bool omitLanguageCode = false,
        IReadOnlyCollection<string>? blockedKeys = null);

    /// <summary>
    /// Every ranked candidate for one language, from every askable provider —
    /// for #841's manual search screen. Unlike <see cref="FetchAsync"/> this
    /// never stops at the first success and never downloads anything; it is
    /// the same ranking read aloud instead of acted on, so a person can see
    /// what Deluno would have picked and choose differently.
    /// </summary>
    Task<IReadOnlyList<SubtitleSearchCandidate>> SearchCandidatesAsync(
        SubtitleSearchRequest request,
        string language,
        bool isEpisodeMedia,
        string? videoPath,
        bool excludeHearingImpaired,
        CancellationToken cancellationToken,
        SubtitleNamePolicy? namePolicy = null,
        IReadOnlyCollection<string>? blockedKeys = null);

    /// <summary>
    /// Downloads and writes one specific candidate a person chose from
    /// <see cref="SearchCandidatesAsync"/>'s results (#841) — the same
    /// malformed-response check, content policy and naming <see cref="FetchAsync"/>
    /// applies to an automatic fetch, so a manual pick is never a second,
    /// looser write path.
    /// </summary>
    Task<SubtitleFetchOutcome> DownloadCandidateAsync(
        SubtitleSearchRequest request,
        string providerKey,
        string downloadToken,
        string language,
        bool isEpisodeMedia,
        string videoPath,
        CancellationToken cancellationToken,
        SubtitleContentModificationPolicy? contentPolicy = null,
        SubtitleNamePolicy? namePolicy = null,
        bool omitLanguageCode = false);
}

/// <summary>One ranked candidate, shown rather than acted on (#841).</summary>
public sealed record SubtitleSearchCandidate(
    string ProviderKey,
    string ProviderName,
    string BlockKey,
    string DownloadToken,
    string Language,
    bool HearingImpaired,
    string? ReleaseName,
    string? Uploader,
    int? DownloadCount,
    SubtitleMatch Match,
    string MatchDescription);

public sealed class SubtitleFetchService(
    ISubtitleProviderRegistry registry,
    ISubtitleProviderRepository repository,
    ISubtitleFileWriter fileWriter,
    TimeProvider timeProvider,
    ILogger<SubtitleFetchService> logger,
    IActivityFeedRepository? activityFeedRepository = null)
    : ISubtitleFetchService
{
    public async Task<SubtitleFetchOutcome> FetchAsync(
        SubtitleSearchRequest request,
        string language,
        bool isEpisodeMedia,
        string videoPath,
        string hearingImpairedPreference,
        CancellationToken cancellationToken,
        SubtitleContentModificationPolicy? contentPolicy = null,
        SubtitleNamePolicy? namePolicy = null,
        bool omitLanguageCode = false,
        IReadOnlyCollection<string>? blockedKeys = null)
    {
        var hiPreference = SubtitleHearingImpairedPreferences.Normalize(hearingImpairedPreference);
        // A hearing-impaired candidate this policy will strip back to plain
        // text reads as plain to everything downstream of the modifier, so
        // "plain only" should not refuse it — by the time it is written it is
        // no longer hearing-impaired (#923). The written outcome still
        // reports the original track's own flag, from `pick.HearingImpaired`
        // below, not this rule.
        var stripsHearingImpaired = contentPolicy?.StripHearingImpairedAnnotations == true;
        var rankHiPreference = hiPreference == SubtitleHearingImpairedPreferences.PlainOnly && stripsHearingImpaired
            ? SubtitleHearingImpairedPreferences.PlainPreferred
            : hiPreference;
        var blocked = blockedKeys is null or { Count: 0 }
            ? null
            : new HashSet<string>(blockedKeys, StringComparer.OrdinalIgnoreCase);
        var rejected = new List<RejectedSubtitleCandidate>();
        var now = timeProvider.GetUtcNow();
        var names = SubtitleNamePolicyCodec.Normalize(namePolicy);
        var configured = await repository.ListAsync(cancellationToken);

        var askable = configured
            .Where(item => item.IsAskable(now))
            .OrderBy(item => item.Priority)
            .Select(item => (Connection: item, Provider: registry.Find(item.ProviderKey)))
            .Where(pair => pair.Provider is not null && Serves(pair.Provider!, isEpisodeMedia))
            .ToArray();

        if (askable.Length == 0)
        {
            var unreadable = configured.FirstOrDefault(item => item.IsEnabled && item.SecretUnreadable);
            if (unreadable is not null)
            {
                var unreadableMessage = unreadable.SecretProblem
                    ?? Deluno.Security.SecretUnreadable.Message("API key");
                return new SubtitleFetchOutcome(
                    language, false, null, null, false, $"{unreadable.Name}: {unreadableMessage}",
                    Failures: [IntegrationFailureFactory.FromLegacy(
                        "subtitle", unreadable.ProviderKey, unreadable.Name, "search",
                        "authentication", unreadableMessage)]);
            }

            var failure = IntegrationFailureFactory.FromLegacy(
                "subtitle",
                "provider-registry",
                "Subtitle providers",
                "search",
                "configuration",
                configured.Count == 0
                    ? "No subtitle providers are configured."
                    : "No enabled provider covers this kind of title right now.");

            return new SubtitleFetchOutcome(
                language, false, null, null, false,
                configured.Count == 0
                    ? "No subtitle providers are set up yet."
                    : "No enabled provider covers this kind of title right now.",
                Failures: [failure]);
        }

        var failures = new List<IntegrationFailure>();

        foreach (var (connection, provider) in askable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = "search";

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

                // Ranked once, tried in order. A candidate that turns out not to
                // be a real subtitle costs one download and one blocklist entry
                // (#898) — it does not cost the rest of this provider's
                // candidates, which the old "give up on the first pick" shape
                // did, nor the rest of this file's languages, which a
                // provider-level failure would have.
                var ranked = Rank(candidates, language, rankHiPreference, videoPath, names)
                    .Where(candidate => blocked is null || !blocked.Contains(SubtitleBlockKeys.For(provider.Key, candidate.DownloadToken)))
                    .ToArray();

                foreach (var pick in ranked)
                {
                    var blockKey = SubtitleBlockKeys.For(provider.Key, pick.DownloadToken);

                    operation = "download";
                    var payload = await provider.DownloadAsync(pick, Credentials(connection), cancellationToken);
                    var subtitle = SubtitleArchive.Extract(payload);

                    if (subtitle is null || !SubtitleArchive.LooksLikeSubtitle(subtitle))
                    {
                        // A rate limit, a captcha or a "sign in to download" page,
                        // served with a 200. Writing it would turn the bar green
                        // over a file that shows a player nothing. Blocklisted so
                        // the next search does not download the same bad file
                        // again, and the next candidate from this same provider is
                        // tried before giving up on it entirely.
                        var failure = IntegrationFailureFactory.FromLegacy(
                            "subtitle",
                            provider.Key,
                            provider.DisplayName,
                            operation,
                            "malformed-response",
                            $"{provider.DisplayName} returned something that is not a subtitle.");
                        failures.Add(failure);
                        rejected.Add(new RejectedSubtitleCandidate(
                            blockKey, language, provider.Key, pick.ReleaseName, failure.Message));
                        await RecordAsync(connection, "degraded", failure.Message, false, null, cancellationToken, failure);
                        continue;
                    }

                    operation = "write";
                    var modification = SubtitleContentModifier.Apply(subtitle, contentPolicy);
                    subtitle = modification.Content;
                    // Stripped back to plain text, so it is written and
                    // recorded as plain — not as a hearing-impaired track that
                    // happens to read like one (#923's strip-counts-as-plain
                    // rule applies to what Deluno wrote, not just to what it
                    // was willing to pick).
                    var writtenAsHearingImpaired = pick.HearingImpaired
                        && !modification.AppliedRules.Contains("hearing-impaired annotations");
                    var written = await fileWriter.WriteAsync(
                        videoPath, language, writtenAsHearingImpaired, subtitle, cancellationToken, omitLanguageCode);

                    await RecordAsync(connection, "healthy",
                        $"Fetched a {language} subtitle for {request.Title}.", true, null, cancellationToken);

                    var match = SubtitleMatchRanking.Rank(pick.ReleaseName, videoPath);

                    return new SubtitleFetchOutcome(
                        Language: language,
                        Found: true,
                        ProviderKey: provider.Key,
                        WrittenPath: written,
                        HearingImpaired: writtenAsHearingImpaired,
                        // The rung is said out loud, because "found it" and "found one
                        // that is in time" are different sentences and only one of
                        // them is worth trusting.
                        Reason: BuildSuccessReason(provider.DisplayName, match, modification),
                        Match: match,
                        Failures: failures.Count == 0 ? null : failures.ToArray(),
                        AppliedModifications: modification.AppliedRules.Count == 0 ? null : modification.AppliedRules,
                        SourceKey: blockKey,
                        RejectedCandidates: rejected.Count == 0 ? null : rejected.ToArray());
                }
            }
            catch (SubtitleProviderRateLimitedException rateLimited)
            {
                // Working, and asked to be left alone. An hour unless it said
                // otherwise — long enough to matter and short enough that a
                // provider is not lost for a day over one busy minute.
                var until = now.Add(rateLimited.RetryAfter ?? TimeSpan.FromHours(1));
                var failure = IntegrationFailureFactory.FromLegacy(
                    "subtitle",
                    provider!.Key,
                    provider.DisplayName,
                    operation,
                    "rate-limited",
                    $"{provider.DisplayName} is rate limiting Deluno until {until:HH:mm}.",
                    retryAfterUtc: until);
                failures.Add(failure);
                await RecordAsync(connection, "rate-limited",
                    failure.Message, true, until, cancellationToken, failure);
            }
            catch (SubtitleProviderQuotaExhaustedException quotaExhausted)
            {
                // The account's own daily allowance, not a login and not a
                // minute-scale rate limit (#946). Paused with the reset time
                // the provider gave, or the next UTC midnight when it did
                // not say — the same pause Authentication uses, so it comes
                // back on its own once that time passes, or the moment a
                // person turns it back on themselves.
                var until = quotaExhausted.ResetUtc ?? NextUtcMidnight(now);
                var failure = IntegrationFailureFactory.QuotaExceeded(
                    "subtitle",
                    provider!.Key,
                    provider.DisplayName,
                    operation,
                    until,
                    $"{provider.DisplayName}'s daily quota is used up: {quotaExhausted.Message}");
                failures.Add(failure);
                await RecordAsync(connection, "degraded", failure.Message, false, until, cancellationToken, failure);
                await NoteIfPausedAsync(provider!, failure, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Subtitle provider {Provider} failed for {Title}.", provider!.Key, request.Title);
                var failure = IntegrationFailureFactory.FromException(
                    "subtitle",
                    provider.Key,
                    provider.DisplayName,
                    operation,
                    exception,
                    retryScheduled: true);
                failures.Add(failure);
                await RecordAsync(connection, "failed", failure.Message, false, failure.RetryAfterUtc, cancellationToken, failure);
                await NoteIfPausedAsync(provider!, failure, cancellationToken);
            }
        }

        var reason = $"No provider had a {language} subtitle for this one.";
        if (failures.Count > 0)
        {
            reason += $" Last provider failure: {failures[^1].Message}";
        }

        return new SubtitleFetchOutcome(
            language, false, null, null, false,
            reason,
            Failures: failures.ToArray(),
            RejectedCandidates: rejected.Count == 0 ? null : rejected.ToArray());
    }

    /// <summary>
    /// Same shape as <see cref="FetchAsync"/> — priority order, first usable
    /// candidate wins, a malformed download is blocklisted and the next one
    /// tried — but ranking only ever considers a forced candidate, and the
    /// file is written with <c>forced: true</c> so it never overwrites the
    /// full subtitle for the same language and is never read back as
    /// coverage.
    /// </summary>
    public async Task<SubtitleFetchOutcome> FetchForcedAsync(
        SubtitleSearchRequest request,
        string language,
        bool isEpisodeMedia,
        string videoPath,
        CancellationToken cancellationToken,
        SubtitleContentModificationPolicy? contentPolicy = null,
        SubtitleNamePolicy? namePolicy = null,
        bool omitLanguageCode = false,
        IReadOnlyCollection<string>? blockedKeys = null)
    {
        var blocked = blockedKeys is null or { Count: 0 }
            ? null
            : new HashSet<string>(blockedKeys, StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();
        var names = SubtitleNamePolicyCodec.Normalize(namePolicy);
        var configured = await repository.ListAsync(cancellationToken);

        var askable = configured
            .Where(item => item.IsAskable(now))
            .OrderBy(item => item.Priority)
            .Select(item => (Connection: item, Provider: registry.Find(item.ProviderKey)))
            .Where(pair => pair.Provider is not null && Serves(pair.Provider!, isEpisodeMedia))
            .ToArray();

        var failures = new List<IntegrationFailure>();

        foreach (var (connection, provider) in askable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var candidates = await provider!.SearchAsync(
                    request with { Languages = [language] },
                    Credentials(connection),
                    cancellationToken);

                var ranked = candidates
                    .Where(candidate => candidate.Forced)
                    .Where(candidate => namePolicy?.Accepts(candidate.ReleaseName) ?? true)
                    .Where(candidate => candidate.Language.StartsWith(language[..Math.Min(2, language.Length)], StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(candidate => SubtitleMatchRanking.Rank(candidate.ReleaseName, videoPath))
                    .ThenByDescending(candidate => candidate.DownloadCount ?? 0)
                    .Where(candidate => blocked is null || !blocked.Contains(SubtitleBlockKeys.For(provider.Key, candidate.DownloadToken)))
                    .ToArray();

                foreach (var pick in ranked)
                {
                    var blockKey = SubtitleBlockKeys.For(provider.Key, pick.DownloadToken);
                    var payload = await provider.DownloadAsync(pick, Credentials(connection), cancellationToken);
                    var subtitle = SubtitleArchive.Extract(payload);

                    if (subtitle is null || !SubtitleArchive.LooksLikeSubtitle(subtitle))
                    {
                        var badFailure = IntegrationFailureFactory.FromLegacy(
                            "subtitle", provider.Key, provider.DisplayName, "download", "malformed-response",
                            $"{provider.DisplayName} returned something that is not a subtitle.");
                        failures.Add(badFailure);
                        continue;
                    }

                    var modification = SubtitleContentModifier.Apply(subtitle, contentPolicy);
                    subtitle = modification.Content;
                    var written = await fileWriter.WriteAsync(
                        videoPath, language, hearingImpaired: false, subtitle, cancellationToken, omitLanguageCode, forced: true);

                    return new SubtitleFetchOutcome(
                        Language: language,
                        Found: true,
                        ProviderKey: provider.Key,
                        WrittenPath: written,
                        HearingImpaired: false,
                        Reason: $"Fetched a forced {language} track from {provider.DisplayName}.",
                        Match: SubtitleMatchRanking.Rank(pick.ReleaseName, videoPath),
                        Failures: failures.Count == 0 ? null : failures.ToArray(),
                        AppliedModifications: modification.AppliedRules.Count == 0 ? null : modification.AppliedRules,
                        SourceKey: blockKey);
                }
            }
            catch (SubtitleProviderRateLimitedException)
            {
                // A forced fetch never counts against a provider's health —
                // the main-language fetch for this same provider already
                // will, and recording it twice would double-count one busy
                // minute as two.
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Forced-subtitle provider {Provider} failed for {Title}.", provider!.Key, request.Title);
                failures.Add(IntegrationFailureFactory.FromException(
                    "subtitle", provider.Key, provider.DisplayName, "search", exception, retryScheduled: true));
            }
        }

        return new SubtitleFetchOutcome(
            language, false, null, null, false,
            $"No forced {language} track was found.",
            Failures: failures.Count == 0 ? null : failures.ToArray());
    }

    /// <summary>
    /// Every ranked candidate from every askable provider, for #841's manual
    /// search — the same ranking <see cref="FetchAsync"/> uses, shown rather
    /// than acted on. Providers are asked in parallel-shaped sequence here
    /// too (one after another, same as a fetch) because this still spends
    /// each provider's own daily allowance, just without a download.
    /// </summary>
    public async Task<IReadOnlyList<SubtitleSearchCandidate>> SearchCandidatesAsync(
        SubtitleSearchRequest request,
        string language,
        bool isEpisodeMedia,
        string? videoPath,
        bool excludeHearingImpaired,
        CancellationToken cancellationToken,
        SubtitleNamePolicy? namePolicy = null,
        IReadOnlyCollection<string>? blockedKeys = null)
    {
        var blocked = blockedKeys is null or { Count: 0 }
            ? null
            : new HashSet<string>(blockedKeys, StringComparer.OrdinalIgnoreCase);
        var names = SubtitleNamePolicyCodec.Normalize(namePolicy);
        var configured = await repository.ListAsync(cancellationToken);
        // Rank's hearing-impaired filter now takes the full preference vocabulary
        // (#836, #923); a manual search only ever asks a plain yes/no, so it
        // maps onto the two ends of that scale rather than the middle ground.
        var hearingImpairedPreference = excludeHearingImpaired
            ? SubtitleHearingImpairedPreferences.PlainOnly
            : SubtitleHearingImpairedPreferences.PlainPreferred;

        var askable = configured
            .Where(item => item.IsAskable(timeProvider.GetUtcNow()))
            .OrderBy(item => item.Priority)
            .Select(item => (Connection: item, Provider: registry.Find(item.ProviderKey)))
            .Where(pair => pair.Provider is not null && Serves(pair.Provider!, isEpisodeMedia))
            .ToArray();

        var results = new List<SubtitleSearchCandidate>();
        foreach (var (connection, provider) in askable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SubtitleCandidate> candidates;
            try
            {
                candidates = await provider!.SearchAsync(
                    request with { Languages = [language] }, Credentials(connection), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception, "Subtitle provider {Provider} failed a manual search for {Title}.", provider!.Key, request.Title);
                continue;
            }

            results.AddRange(Rank(candidates, language, hearingImpairedPreference, videoPath, names)
                .Where(candidate => blocked is null || !blocked.Contains(SubtitleBlockKeys.For(provider!.Key, candidate.DownloadToken)))
                .Select(candidate =>
                {
                    var match = SubtitleMatchRanking.Rank(candidate.ReleaseName, videoPath);
                    return new SubtitleSearchCandidate(
                        provider!.Key,
                        provider.DisplayName,
                        SubtitleBlockKeys.For(provider.Key, candidate.DownloadToken),
                        candidate.DownloadToken,
                        candidate.Language,
                        candidate.HearingImpaired,
                        candidate.ReleaseName,
                        candidate.Uploader,
                        candidate.DownloadCount,
                        match,
                        SubtitleMatchRanking.Describe(match));
                }));
        }

        return results;
    }

    /// <summary>
    /// Downloads and writes one specific candidate a person picked from
    /// <see cref="SearchCandidatesAsync"/> (#841). Re-searches the one
    /// provider named rather than trusting a candidate serialised across an
    /// API call, so a release that vanished between browsing and picking
    /// fails honestly instead of downloading whatever token happened to
    /// still resolve.
    /// </summary>
    public async Task<SubtitleFetchOutcome> DownloadCandidateAsync(
        SubtitleSearchRequest request,
        string providerKey,
        string downloadToken,
        string language,
        bool isEpisodeMedia,
        string videoPath,
        CancellationToken cancellationToken,
        SubtitleContentModificationPolicy? contentPolicy = null,
        SubtitleNamePolicy? namePolicy = null,
        bool omitLanguageCode = false)
    {
        var configured = await repository.ListAsync(cancellationToken);
        var connection = configured.FirstOrDefault(item =>
            string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
        var provider = registry.Find(providerKey);

        if (connection is { SecretUnreadable: true })
        {
            return new SubtitleFetchOutcome(
                language, false, null, null, false,
                $"{connection.Name}: {connection.SecretProblem ?? Deluno.Security.SecretUnreadable.Message("API key")}");
        }

        if (connection is null || provider is null || !Serves(provider, isEpisodeMedia))
        {
            return new SubtitleFetchOutcome(
                language, false, null, null, false, $"{providerKey} is not a configured, enabled subtitle provider.");
        }

        IReadOnlyList<SubtitleCandidate> candidates;
        try
        {
            candidates = await provider.SearchAsync(
                request with { Languages = [language] }, Credentials(connection), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new SubtitleFetchOutcome(
                language, false, null, null, false,
                $"{provider.DisplayName} could not be searched again: {exception.Message}");
        }

        var pick = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.DownloadToken, downloadToken, StringComparison.Ordinal));
        if (pick is null)
        {
            return new SubtitleFetchOutcome(
                language, false, null, null, false,
                $"{provider.DisplayName} no longer offers that subtitle - it may have been removed or replaced since it was found.");
        }

        var blockKey = SubtitleBlockKeys.For(provider.Key, pick.DownloadToken);
        var payload = await provider.DownloadAsync(pick, Credentials(connection), cancellationToken);
        var subtitle = SubtitleArchive.Extract(payload);

        if (subtitle is null || !SubtitleArchive.LooksLikeSubtitle(subtitle))
        {
            return new SubtitleFetchOutcome(
                language, false, null, null, false,
                $"{provider.DisplayName} returned something that is not a subtitle.",
                RejectedCandidates: [new RejectedSubtitleCandidate(blockKey, language, provider.Key, pick.ReleaseName, "Not a real subtitle file.")]);
        }

        var modification = SubtitleContentModifier.Apply(subtitle, contentPolicy);
        subtitle = modification.Content;
        var written = await fileWriter.WriteAsync(
            videoPath, language, pick.HearingImpaired, subtitle, cancellationToken, omitLanguageCode);
        var match = SubtitleMatchRanking.Rank(pick.ReleaseName, videoPath);

        return new SubtitleFetchOutcome(
            Language: language,
            Found: true,
            ProviderKey: provider.Key,
            WrittenPath: written,
            HearingImpaired: pick.HearingImpaired,
            Reason: BuildSuccessReason(provider.DisplayName, match, modification),
            Match: match,
            AppliedModifications: modification.AppliedRules.Count == 0 ? null : modification.AppliedRules,
            SourceKey: blockKey);
    }

    /// <summary>
    /// The best of what came back.
    ///
    /// <para><b>How well it matches the file comes first now.</b> This used to
    /// sort by download count, with a note admitting it was "a crude proxy for
    /// this one is in time" and that release matching was the better rule. It is,
    /// and reading Bazarr's scoring settled what release matching means — see
    /// <see cref="SubtitleMatchRanking"/>. Download count survives underneath as
    /// the tiebreaker it always should have been, which is also where Bazarr puts
    /// its own one-point weights.</para>
    ///
    /// <para>A plain track before a hearing-impaired one where both are on the
    /// same rung. Hearing impaired <i>is</i> coverage — it is watchable, Deluno
    /// counts it, and Bazarr scores it at a single point — but it is not what
    /// most people would pick if asked, and it is the one choice here somebody
    /// would notice being made for them. It never outranks a better fit, though:
    /// a hearing-impaired subtitle cut for your exact release beats a plain one
    /// that is forty seconds out.</para>
    ///
    /// <para>Forced is never chosen: a forced track is four lines of Elvish, and
    /// the rest of Deluno already refuses to count it as coverage.</para>
    ///
    /// <para><b>Hearing-impaired is a per-language choice, not a fixed rule
    /// (#836).</b> <see cref="SubtitleHearingImpairedPreferences.PlainOnly"/>
    /// refuses a hearing-impaired candidate outright — the wrong type is never
    /// picked, so the next candidate is tried instead of it, which is what
    /// #923 asks for without needing a second mechanism.
    /// <see cref="SubtitleHearingImpairedPreferences.PlainPreferred"/> is the
    /// default above: either is coverage, plain wins a tie.
    /// <see cref="SubtitleHearingImpairedPreferences.HearingImpairedPreferred"/>
    /// is the same list with that tiebreak inverted.</para>
    /// </summary>
    private static IEnumerable<SubtitleCandidate> Rank(
        IReadOnlyList<SubtitleCandidate> candidates,
        string language,
        string hearingImpairedPreference,
        string? videoPath,
        SubtitleNamePolicy? namePolicy = null)
        => candidates
            .Where(candidate => !candidate.Forced)
            .Where(candidate => hearingImpairedPreference != SubtitleHearingImpairedPreferences.PlainOnly || !candidate.HearingImpaired)
            // The library's own words about release names. Applied before
            // ranking rather than after, so a refused release cannot win on
            // filename similarity and then be discarded.
            .Where(candidate => namePolicy?.Accepts(candidate.ReleaseName) ?? true)
            .Where(candidate => candidate.Language.StartsWith(language[..Math.Min(2, language.Length)], StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => SubtitleMatchRanking.Rank(candidate.ReleaseName, videoPath))
            .ThenBy(candidate => hearingImpairedPreference == SubtitleHearingImpairedPreferences.HearingImpairedPreferred
                ? (candidate.HearingImpaired ? 0 : 1)
                : (candidate.HearingImpaired ? 1 : 0))
            .ThenByDescending(candidate => candidate.DownloadCount ?? 0);

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

    /// <summary>
    /// The reset time a quota exception did not give one. OpenSubtitles
    /// documents its allowance as a rolling 24 hours, but every quota answer
    /// Deluno has actually seen carries its own reset timestamp (#946); UTC
    /// midnight is only ever used for the "accepted but no link" shape,
    /// which does not.
    /// </summary>
    private static DateTimeOffset NextUtcMidnight(DateTimeOffset now)
        => new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    private static string BuildSuccessReason(
        string providerName,
        SubtitleMatch match,
        SubtitleContentModificationResult modification)
    {
        var reason = $"Found by {providerName}. {SubtitleMatchRanking.Describe(match)}";
        return modification.AppliedRules.Count == 0
            ? reason
            : $"{reason} Applied: {string.Join(", ", modification.AppliedRules)}.";
    }

    private static SubtitleProviderCredentials Credentials(SubtitleProviderConnection connection)
        => new(connection.Username, connection.Secret, connection.ApiKey);

    private Task RecordAsync(
        SubtitleProviderConnection connection,
        string status,
        string message,
        bool success,
        DateTimeOffset? rateLimitedUntil,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null)
        => repository.RecordHealthAsync(
            connection.ProviderKey, status, message, latencyMs: null, success, rateLimitedUntil, cancellationToken, failure);

    /// <summary>
    /// Says so in Activity when the health record just written paused this
    /// provider (#896). The repository is the one place that actually flips
    /// <c>is_enabled</c> off — see
    /// <see cref="Deluno.Connections.Data.ISubtitleProviderRepository.RecordHealthAsync"/> —
    /// so this only decides whether to say so, from the same fact the
    /// repository acted on.
    ///
    /// <para>A wrong login (<see cref="IntegrationFailureKind.Authentication"/>)
    /// and a used-up quota (<see cref="IntegrationFailureKind.QuotaExceeded"/>,
    /// #946) both get paused and both write this same Activity line — asking
    /// again would waste a search attempt every cycle for a source that
    /// cannot answer. They differ only in how they come back: a login needs a
    /// person to fix it, a quota resets on its own.</para>
    /// </summary>
    private async Task NoteIfPausedAsync(
        ISubtitleProvider provider, IntegrationFailure failure, CancellationToken cancellationToken)
    {
        if (failure.Kind is not (IntegrationFailureKind.Authentication or IntegrationFailureKind.QuotaExceeded)
            || activityFeedRepository is null)
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            "subtitle.provider.paused",
            $"{provider.DisplayName}'s login is wrong or its quota is used up, so Deluno paused it: {failure.Message} Turn it back on under Subtitle providers once this is fixed.",
            null,
            null,
            "subtitle-provider",
            provider.Key,
            cancellationToken);
    }
}
