using System.Data.Common;
using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Microsoft.Extensions.Logging;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Media;

/// <summary>
/// Auto-tagging (#1024), the one place every add path, edit and refresh calls.
/// It reads what Deluno knows about a title, asks <see cref="AutoTagMatcher"/>
/// which tags the rules want, gives or takes them through
/// <see cref="ITitleTagService"/> and writes one Activity line in plain words
/// when anything changed. With no rules (a new install, #936) it does nothing
/// at all, and never opens a catalogue.
/// </summary>
public sealed class AutoTaggingService(
    IAutoTagRuleStore ruleStore,
    ITitleTagService titleTags,
    IMediaTagStore mediaTagStore,
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILibrariesRepository librariesRepository,
    IActivityFeedRepository activityFeedRepository,
    ILogger<AutoTaggingService> logger) : IAutoTaggingService
{
    public async Task<AutoTagOutcome> ApplyAsync(
        MediaKind kind,
        string mediaId,
        AutoTagTrigger trigger,
        AutoTagAddFacts? add,
        IReadOnlyList<string>? libraryIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return AutoTagOutcome.None;
        }

        try
        {
            var rules = await ruleStore.ListAsync(cancellationToken);
            if (!rules.Any(rule => rule.IsEnabled && AutoTagScopes.Covers(rule.AppliesTo, kind)))
            {
                return AutoTagOutcome.None;
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var facts = await ReadFactsAsync(kind, mediaId, add, libraryIds, libraries, cancellationToken);
            if (facts is null)
            {
                return AutoTagOutcome.None;
            }

            return await ApplyRulesAsync(rules, facts, mediaId, trigger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A title must never fail to be added, edited or refreshed because of a tagging rule.
            logger.LogWarning(ex, "Auto-tagging {Kind} {MediaId} failed.", kind, mediaId);
            return AutoTagOutcome.None;
        }
    }

    public async Task<IReadOnlyList<string>> PredictAsync(AutoTagFacts facts, CancellationToken cancellationToken)
    {
        try
        {
            var rules = await ruleStore.ListAsync(cancellationToken);
            return AutoTagMatcher.Changes(rules, facts).Add.Select(change => change.Tag).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Predicting auto-tags for {Title} failed.", facts.Title);
            return [];
        }
    }

    public async Task<int> ApplyToAllAsync(MediaKind? kind, CancellationToken cancellationToken)
    {
        var rules = await ruleStore.ListAsync(cancellationToken);
        if (!rules.Any(rule => rule.IsEnabled))
        {
            return 0;
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var changed = 0;
        foreach (var each in new[] { MediaKind.Movie, MediaKind.Series })
        {
            if (kind is not null && kind != each)
            {
                continue;
            }

            foreach (var title in await mediaTagStore.ListAllTitleTagsAsync(each, cancellationToken))
            {
                var facts = await ReadFactsAsync(each, title.Id, null, null, libraries, cancellationToken);
                if (facts is null)
                {
                    continue;
                }

                var outcome = await ApplyRulesAsync(rules, facts, title.Id, AutoTagTrigger.RunNow, cancellationToken);
                if (outcome.Changed)
                {
                    changed++;
                }
            }
        }

        return changed;
    }

    private async Task<AutoTagOutcome> ApplyRulesAsync(
        IReadOnlyList<AutoTagRule> rules,
        AutoTagFacts facts,
        string mediaId,
        AutoTagTrigger trigger,
        CancellationToken cancellationToken)
    {
        var changes = AutoTagMatcher.Changes(rules, facts);
        if (changes.IsEmpty)
        {
            return AutoTagOutcome.None;
        }

        var added = changes.Add.Count == 0
            ? []
            : await titleTags.GiveAsync(facts.Kind, mediaId, changes.Add.Select(change => change.Tag), cancellationToken);
        var removed = changes.Remove.Count == 0
            ? []
            : await titleTags.TakeAwayAsync(facts.Kind, mediaId, changes.Remove.Select(change => change.Tag), cancellationToken);
        if (added.Count == 0 && removed.Count == 0)
        {
            return AutoTagOutcome.None;
        }

        await activityFeedRepository.RecordActivityAsync(
            "tags.auto",
            Describe(facts.Title, changes, added, removed, trigger),
            null,
            null,
            facts.Kind == MediaKind.Movie ? "movie" : "series",
            mediaId,
            cancellationToken);
        return new AutoTagOutcome(added, removed);
    }

    /// <summary>The Activity line, in plain words: what was tagged, why, and when.</summary>
    public static string Describe(string title, AutoTagChanges changes, IReadOnlyList<string> added, IReadOnlyList<string> removed, AutoTagTrigger trigger)
    {
        string ReasonFor(IEnumerable<AutoTagChange> pool, IEnumerable<string> tags) =>
            string.Join(" and ", tags
                .Select(tag => pool.FirstOrDefault(change => string.Equals(change.Tag, tag, StringComparison.OrdinalIgnoreCase))?.RuleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => $"“{name}”"));

        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add($"gave “{title}” the {(added.Count == 1 ? "tag" : "tags")} {string.Join(", ", added.Select(tag => $"“{tag}”"))} because it matches the auto-tagging {(ReasonFor(changes.Add, added).Contains(" and ", StringComparison.Ordinal) ? "rules" : "rule")} {ReasonFor(changes.Add, added)}");
        }

        if (removed.Count > 0)
        {
            var lead = parts.Count == 0 ? $"took the {(removed.Count == 1 ? "tag" : "tags")}" : $"and took the {(removed.Count == 1 ? "tag" : "tags")}";
            parts.Add($"{lead} {string.Join(", ", removed.Select(tag => $"“{tag}”"))} off “{title}” because it no longer matches {ReasonFor(changes.Remove, removed)}");
        }

        var when = trigger switch
        {
            AutoTagTrigger.Added => " when it was added",
            AutoTagTrigger.Edited => " after it was changed",
            AutoTagTrigger.Refreshed => " when its details were refreshed",
            _ => " when the rules were run over the library"
        };
        return $"Auto-tagging {string.Join(" ", parts)}{when}.";
    }

    private async Task<AutoTagFacts?> ReadFactsAsync(
        MediaKind kind,
        string mediaId,
        AutoTagAddFacts? add,
        IReadOnlyList<string>? libraryIds,
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(map.DatabaseName, cancellationToken);

        // Table and column names come from the closed MediaTableMap, never from input.
        var seriesType = kind == MediaKind.Series ? "series_type" : "NULL";
        string title;
        string? genres, language, profile, status, studio, certification, keywords, collection, quality, type;
        bool monitored;
        int? year, runtime;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT title, genres, monitored, original_language, quality_profile_id, {map.YearColumn}, {map.MadeByColumn},
                       status, certification, keywords, runtime_minutes, collection, primary_current_quality, {seriesType}
                FROM {map.EntryTable}
                WHERE id = @id;
                """;
            AddParameter(command, "@id", mediaId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            title = reader.GetString(0);
            genres = Text(reader, 1);
            monitored = !reader.IsDBNull(2) && reader.GetInt64(2) != 0;
            language = Text(reader, 3);
            profile = Text(reader, 4);
            year = Number(reader, 5);
            studio = Text(reader, 6);
            status = Text(reader, 7);
            certification = Text(reader, 8);
            keywords = Text(reader, 9);
            runtime = Number(reader, 10);
            collection = Text(reader, 11);
            quality = Text(reader, 12);
            type = Text(reader, 13);
        }

        var ids = new List<string>(libraryIds ?? []);
        if (ids.Count == 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT library_id FROM {map.WantedTable} WHERE {map.WantedMediaIdColumn} = @id;";
            AddParameter(command, "@id", mediaId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var held = libraries.Where(library => ids.Contains(library.Id, StringComparer.Ordinal)).ToList();
        // A title with no quality profile of its own follows its library's.
        profile ??= held.Select(library => library.QualityProfileId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        var tags = (await mediaTagStore.ListAsync(kind, mediaId, cancellationToken)).Select(tag => tag.Name).ToArray();
        var genreList = SplitList(genres);
        return new AutoTagFacts(
            kind,
            title,
            genreList,
            monitored,
            language,
            profile,
            ids,
            held.Select(library => library.RootPath).ToArray(),
            type,
            status,
            tags,
            year,
            SplitList(keywords),
            runtime,
            studio,
            certification,
            AutoTagFactsBuilder.LooksLikeAnime(type, genreList, language),
            collection,
            quality is not null && (quality.Contains("2160", StringComparison.OrdinalIgnoreCase) || quality.Contains("4k", StringComparison.OrdinalIgnoreCase) || quality.Contains("uhd", StringComparison.OrdinalIgnoreCase)),
            add);
    }

    private static string? Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int? Number(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
