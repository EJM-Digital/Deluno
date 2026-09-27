using System.Globalization;
using System.Runtime.InteropServices;
using Deluno.Integrations.MediaServers;

namespace Deluno.Retention;

/// <summary>
/// A condition's answer for one title: it holds, it does not, or it cannot be
/// said (#494, DESIGN-008).
///
/// <para><b>"Cannot say" is never read as "no".</b> A server with no favourites
/// asked whether anyone favourited a film has not said nobody did. So such a
/// condition neither keeps a title nor adds one, and the title is listed as
/// undecided with the reason, instead of a rule for "not favourited" quietly
/// selecting everything on that server.</para>
/// </summary>
public readonly record struct RetentionAnswer(bool? Holds, string Sentence)
{
    public static RetentionAnswer Yes(string sentence) => new(true, sentence);

    public static RetentionAnswer No(string sentence) => new(false, sentence);

    public static RetentionAnswer CannotSay(string sentence) => new(null, sentence);
}

/// <summary>A drive's space, read for the library a title lives in.</summary>
public interface IRetentionDiskSpace
{
    /// <returns>Null when the drive could not be read.</returns>
    (long FreeBytes, long TotalBytes)? Read(string path);
}

public sealed class RetentionDiskSpace : IRetentionDiskSpace
{
    public (long FreeBytes, long TotalBytes)? Read(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // DriveInfo does not take a share (\\server\share); this does, and
                // reports the space the service account may actually use.
                var folder = path.EndsWith('\\') ? path : path + "\\";
                return GetDiskFreeSpaceEx(folder, out var available, out var total, out _) ? ((long)available, (long)total) : null;
            }

            var drive = new DriveInfo(path);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}

/// <summary>Answers the rule fields that are not library filters.</summary>
/// <param name="seerr">Every connected Seerr server's requests; null when none is connected.</param>
/// <param name="qualityRanks">Quality tier name to rank, for <c>part.qualityRank</c> (#834). Empty when the model could not be read.</param>
public sealed class RetentionFieldAnswers(
    IReadOnlyList<RetentionServerIndex> servers,
    IRetentionDiskSpace disk,
    DateTimeOffset now,
    RetentionSeerrIndex? seerr = null,
    IReadOnlyDictionary<string, int>? qualityRanks = null)
{
    private readonly Dictionary<string, (long FreeBytes, long TotalBytes)?> _drives = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, int> _qualityRanks = qualityRanks ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public RetentionAnswer Answer(RetentionUnit unit, RetentionFieldDefinition field, string op, string? value)
        => field.Source switch
        {
            RetentionFieldSources.Disk => Disk(unit, field, op, value),
            RetentionFieldSources.Deluno => Part(unit, field, op, value),
            RetentionFieldSources.Seerr => Seerr(unit, field, op, value),
            _ => Server(unit, field, op, value)
        };

    // ---------------------------------------------------------------- servers
    private RetentionAnswer Server(RetentionUnit unit, RetentionFieldDefinition field, string op, string? value)
    {
        var able = servers.Where(index => field.UnsupportedOn?.Contains(index.Server.Kind, StringComparer.OrdinalIgnoreCase) != true).ToArray();
        if (able.Length == 0)
        {
            return RetentionAnswer.CannotSay($"{field.Label}: {Names(servers)} cannot tell.");
        }

        var matches = able.Select(index => index.Find(unit)).OfType<RetentionServerMatch>().ToArray();
        var where = Names(able);
        if (matches.Length == 0)
        {
            // A title the servers do not hold has not been "never watched" -
            // it may simply not have been scanned yet. Nothing is decided on it.
            return RetentionAnswer.CannotSay($"{field.Label}: not on {where}.");
        }

        switch (field.ValueKind)
        {
            case RetentionValueKinds.Users:
            {
                if (field.Id == "server.watchlistedBy" && matches.All(match => match.Item.WatchlistedBy is null))
                {
                    return RetentionAnswer.CannotSay($"{field.Label}: {where} could not be asked for its watchlist.");
                }

                if (IsSinceAdded(field.Id) && unit.AddedUtc is null)
                {
                    return RetentionAnswer.CannotSay($"{field.Label}: Deluno does not know when it was added.");
                }

                var people = Users(field.Id, matches, IsSinceAdded(field.Id) ? unit.AddedUtc : null);
                var everyone = matches.SelectMany(match => match.Users).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // "Watched by nobody" needs no ": nobody" after it.
                var said = people.Count == 0 ? (op == RetentionOperators.Nobody ? string.Empty : ": nobody") : $": {string.Join(", ", people)}";
                return Holds(Users(op, value, people, everyone), $"{field.Label} {Phrase(field, op, value)}{said}");
            }

            case RetentionValueKinds.TextList:
            {
                var names = Texts(field.Id, matches);
                return Holds(TextList(op, value, names), $"{Lead(field, op, value)}: {(names.Count == 0 ? "none" : string.Join(", ", names))}");
            }

            case RetentionValueKinds.Date:
            {
                var when = DateOf(field.Id, matches);
                return Holds(Date(op, value, when), $"{Lead(field, op, value)}: {(when is null ? "never" : when.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture))}");
            }

            case RetentionValueKinds.Text:
            {
                var text = TextOf(field.Id, matches);
                return Holds(Text(op, value, text), $"{Lead(field, op, value)}: {text ?? "not known"}");
            }

            default:
            {
                var number = NumberOf(field.Id, matches);
                return Holds(Number(op, value, number, 1), $"{Lead(field, op, value)}: {(number is null ? "none" : number.Value.ToString("0.#", CultureInfo.InvariantCulture))}");
            }
        }
    }

    /// <summary>A field a rule can ask "since added" instead of all-time (#834).</summary>
    private static bool IsSinceAdded(string fieldId) => fieldId.EndsWith("SinceAdded", StringComparison.Ordinal);

    /// <param name="sinceAdded">
    /// Set only for the *SinceAdded fields: a play only counts if it happened
    /// after this. Null means every play counts, same as the all-time field.
    /// </param>
    private static IReadOnlyList<string> Users(string fieldId, IReadOnlyList<RetentionServerMatch> matches, DateTimeOffset? sinceAdded = null)
    {
        bool Counts(MediaServerViewer viewer) => sinceAdded is null || (viewer.LastPlayedUtc is { } when && when > sinceAdded);

        IEnumerable<string> people = fieldId switch
        {
            "server.watchedBy" or "server.watchedBySinceAdded" => matches.SelectMany(match => match.Item.Viewers.Where(viewer => viewer.Played && Counts(viewer)).Select(viewer => viewer.User)),
            "server.favouritedBy" => matches.SelectMany(match => match.Item.Viewers.Where(viewer => viewer.Favourite == true).Select(viewer => viewer.User)),
            "server.watchlistedBy" => matches.SelectMany(match => match.Item.WatchlistedBy ?? []),
            "server.watchers" or "server.watchersSinceAdded" => matches.SelectMany(match => match.Episodes.SelectMany(episode => episode.Viewers.Where(viewer => viewer.Played && Counts(viewer)).Select(viewer => viewer.User))),
            "server.allEpisodesSeenBy" or "server.allEpisodesSeenBySinceAdded" => matches.SelectMany(match => match.Users.Where(user =>
                match.Episodes.Count > 0 &&
                match.Episodes.All(episode => episode.Viewers.Any(viewer => viewer.Played && Counts(viewer) && string.Equals(viewer.User, user, StringComparison.OrdinalIgnoreCase))))),
            _ => []
        };
        return people.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> Texts(string fieldId, IReadOnlyList<RetentionServerMatch> matches)
    {
        IEnumerable<string> names = fieldId switch
        {
            "server.people" => matches.SelectMany(match => match.Item.People),
            "server.genres" => matches.SelectMany(match => match.Item.Genres),
            "server.labels" => matches.SelectMany(match => match.Item.Labels),
            "server.collections" => matches.SelectMany(match => match.Item.Collections),
            "server.playlists" => matches.SelectMany(match => match.Item.Playlists.Concat(match.Episodes.SelectMany(episode => episode.Playlists))),
            _ => []
        };
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static DateTimeOffset? DateOf(string fieldId, IReadOnlyList<RetentionServerMatch> matches)
    {
        IEnumerable<DateTimeOffset?> dates = fieldId switch
        {
            // Finished only: a play attempt that was never completed does not count.
            "server.lastViewed" => matches.SelectMany(match => match.Item.Viewers.Where(viewer => viewer.Played).Concat(match.Episodes.SelectMany(episode => episode.Viewers.Where(viewer => viewer.Played))).Select(viewer => viewer.LastPlayedUtc)
                .Append(match.Item.Watch.PlayedByAnyone ? match.Item.Watch.LastPlayedUtc : null)),
            // Finished or not: whoever last touched it at all, including an unfinished attempt.
            "server.lastPlayed" => matches.SelectMany(match => match.Item.Viewers.Concat(match.Episodes.SelectMany(episode => episode.Viewers)).Select(viewer => viewer.LastPlayedUtc)
                .Append(match.Item.Watch.LastPlayedUtc)),
            "server.newestEpisodeViewed" => matches.SelectMany(match => match.Episodes.SelectMany(episode => episode.Viewers).Select(viewer => viewer.LastPlayedUtc)),
            // The earliest a server saw it: it has been there since then.
            "server.added" => [matches.Select(match => match.Item.AddedUtc).Where(when => when is not null).Min()],
            "server.releaseDate" => matches.Select(match => match.Item.ReleaseDate),
            "server.lastEpisodeAdded" => matches.SelectMany(match => match.Episodes.Select(episode => episode.AddedUtc)),
            "server.lastEpisodeAired" => matches.SelectMany(match => match.Episodes.Select(episode => episode.ReleaseDate)),
            _ => []
        };
        return dates.Where(when => when is not null).Max();
    }

    private static string? TextOf(string fieldId, IReadOnlyList<RetentionServerMatch> matches)
        => fieldId switch
        {
            "server.resolution" => matches.Select(match => match.Item.Video.Resolution).FirstOrDefault(text => text is not null),
            "server.codec" => matches.Select(match => match.Item.Video.Codec).FirstOrDefault(text => text is not null),
            _ => null
        };

    private static double? NumberOf(string fieldId, IReadOnlyList<RetentionServerMatch> matches)
    {
        double? First(Func<MediaServerRatings, double?> pick) => matches.Select(match => pick(match.Item.Ratings)).FirstOrDefault(score => score is not null);
        IEnumerable<MediaServerViewer> Everyone(RetentionServerMatch match) => match.Episodes.Count > 0 ? match.Episodes.SelectMany(episode => episode.Viewers) : match.Item.Viewers;

        return fieldId switch
        {
            "server.timesViewed" => matches.Sum(match => Everyone(match).Sum(viewer => viewer.PlayCount)),
            "server.totalViews" => matches.Sum(match => match.Episodes.Sum(episode => episode.Viewers.Sum(viewer => viewer.PlayCount))),
            "server.playAttempts" => matches.Sum(match => Everyone(match).Count(viewer => viewer.InProgress)),
            "server.watchedEpisodes" => matches.Select(match => match.Episodes.Count(episode => episode.Viewers.Any(viewer => viewer.Played))).DefaultIfEmpty(0).Max(),
            "server.availableEpisodes" => matches.Select(match => match.Episodes.Count).DefaultIfEmpty(0).Max(),
            "server.userRating" => matches.SelectMany(match => match.Item.Viewers).Select(viewer => viewer.Rating).Where(score => score is not null).Max(),
            "server.ratingCritic" => First(ratings => ratings.Critic),
            "server.ratingAudience" => First(ratings => ratings.Audience),
            "server.ratingImdb" => First(ratings => ratings.Imdb),
            "server.ratingRottenTomatoesCritic" => First(ratings => ratings.RottenTomatoesCritic),
            "server.ratingRottenTomatoesAudience" => First(ratings => ratings.RottenTomatoesAudience),
            "server.ratingTmdb" => First(ratings => ratings.Tmdb),
            "server.collectionCount" => matches.SelectMany(match => match.Item.Collections).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "server.playlistCount" => Texts("server.playlists", matches).Count,
            "server.bitrate" => matches.Select(match => (double?)match.Item.Video.BitrateKbps).FirstOrDefault(rate => rate is not null),
            _ => null
        };
    }

    private static string Names(IEnumerable<RetentionServerIndex> indexes)
        => string.Join(", ", indexes.Select(index => index.Server.Name));

    // ------------------------------------------------------------------- disk
    private RetentionAnswer Disk(RetentionUnit unit, RetentionFieldDefinition field, string op, string? value)
    {
        var root = unit.LibraryRoots.FirstOrDefault();
        if (root is null)
        {
            return RetentionAnswer.CannotSay($"{field.Label}: Deluno does not know which library holds it.");
        }

        if (!_drives.TryGetValue(root, out var space))
        {
            _drives[root] = space = disk.Read(root);
        }

        if (space is null)
        {
            return RetentionAnswer.CannotSay($"{field.Label}: the drive at {root} could not be read.");
        }

        var (free, total) = space.Value;
        return field.Id switch
        {
            "disk.freePercent" => Holds(Number(op, value, total <= 0 ? null : Math.Round(free * 100d / total, 1), 1), $"{Lead(field, op, value)}: {(total <= 0 ? 0 : free * 100d / total):0.#}%"),
            "disk.totalGb" => Holds(Number(op, value, total, GigaByte), $"{Lead(field, op, value)}: {total / (double)GigaByte:0.#} GB"),
            _ => Holds(Number(op, value, free, GigaByte), $"{Lead(field, op, value)}: {free / (double)GigaByte:0.#} GB free")
        };
    }

    // -------------------------------------------------------------------- Seerr
    private RetentionAnswer Seerr(RetentionUnit unit, RetentionFieldDefinition field, string op, string? value)
    {
        if (seerr is null)
        {
            return RetentionAnswer.CannotSay($"{field.Label}: no Seerr server is connected.");
        }

        // Seerr's list of requests is complete, so no request is an answer:
        // nobody asked for it. A title with no id Seerr could know it by is not.
        var requests = seerr.Find(unit);
        if (requests is null)
        {
            return RetentionAnswer.CannotSay($"{field.Label}: Deluno has no TMDb or TVDb id Seerr could know it by.");
        }

        var phrase = Lead(field, op, value);
        switch (field.Id)
        {
            case "seerr.requestedBy":
            {
                var people = requests.Select(request => request.RequestedBy).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var said = people.Length == 0 ? (op == RetentionOperators.Nobody ? string.Empty : ": nobody") : $": {string.Join(", ", people)}";
                return Holds(Users(op, value, people, seerr.Users), $"{field.Label} {Phrase(field, op, value)}{said}");
            }

            case "seerr.requestDate":
            case "seerr.approvalDate":
            {
                var when = field.Id == "seerr.requestDate"
                    ? requests.Select(request => (DateTimeOffset?)request.RequestedUtc).Max()
                    : requests.Select(request => request.ApprovedUtc).Max();
                return Holds(Date(op, value, when), $"{phrase}: {(when is null ? "never" : when.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture))}");
            }

            case "seerr.isRequested":
                return Holds(Boolean(value, requests.Count > 0), $"{phrase}: {(requests.Count > 0 ? "yes" : "no")}");

            default:
                return Holds(Number(op, value, requests.Count, 1), $"{phrase}: {requests.Count}");
        }
    }

    // ------------------------------------------------------- seasons, episodes
    private RetentionAnswer Part(RetentionUnit unit, RetentionFieldDefinition field, string op, string? value)
    {
        var part = unit.Part;
        if (part is null)
        {
            return RetentionAnswer.CannotSay($"{field.Label} only applies to seasons and episodes.");
        }

        var phrase = Lead(field, op, value);
        return field.Id switch
        {
            "part.seasonNumber" => Holds(Number(op, value, unit.SeasonNumber, 1), $"{phrase}: {unit.SeasonNumber}"),
            "part.episodeNumber" => Holds(Number(op, value, unit.EpisodeNumber, 1), $"{phrase}: {unit.EpisodeNumber}"),
            "part.latestSeason" => Holds(Boolean(value, part.LatestSeason), $"{phrase}: {(part.LatestSeason ? "yes" : "no")}"),
            "part.hasUnaired" => Holds(Boolean(value, part.HasUnaired), $"{phrase}: {(part.HasUnaired ? "yes" : "no")}"),
            "part.size" => Holds(Number(op, value, unit.SizeBytes, GigaByte), $"{phrase}: {(unit.SizeBytes ?? 0) / (double)GigaByte:0.#} GB"),
            "part.episodesHeld" => Holds(Number(op, value, part.EpisodesHeld, 1), $"{phrase}: {part.EpisodesHeld}"),
            "part.airDate" => Holds(Date(op, value, part.AirDate), $"{phrase}: {part.AirDate?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "not known"}"),
            "part.imported" => Holds(Date(op, value, part.Imported), $"{phrase}: {part.Imported?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "not known"}"),
            "part.quality" => Holds(Text(op, value, part.Quality), $"{phrase}: {part.Quality ?? "not known"}"),
            "part.qualityRank" => QualityRank(part.Quality) is { } rank
                ? Holds(Number(op, value, rank, 1), $"{phrase}: {rank.ToString(CultureInfo.InvariantCulture)}")
                : RetentionAnswer.CannotSay($"{field.Label}: not known."),
            "part.path" => Holds(Text(op, value, part.Path), $"{phrase}: {part.Path ?? "not known"}"),
            "part.cutoffMet" => part.CutoffMet is { } met ? Holds(Boolean(value, met), $"{phrase}: {(met ? "yes" : "no")}") : RetentionAnswer.CannotSay($"{field.Label}: not known."),
            "part.monitored" => part.Monitored is { } monitored ? Holds(Boolean(value, monitored), $"{phrase}: {(monitored ? "yes" : "no")}") : RetentionAnswer.CannotSay($"{field.Label}: not known."),
            _ => RetentionAnswer.CannotSay($"{field.Label} is not something Deluno can answer.")
        };
    }

    /// <summary>The quality model's rank for a tier name, so a rule can threshold on it numerically (#834).</summary>
    private int? QualityRank(string? quality)
        => quality is not null && _qualityRanks.TryGetValue(quality, out var rank) ? rank : null;

    // -------------------------------------------------------------- operators
    private const long GigaByte = 1L << 30;

    private static RetentionAnswer Holds(bool holds, string sentence) => holds ? RetentionAnswer.Yes(sentence) : RetentionAnswer.No(sentence);

    private static string[] Values(string? value)
        => (value ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static bool Users(string op, string? value, IReadOnlyList<string> people, IReadOnlyList<string> everyone)
        => op switch
        {
            RetentionOperators.Nobody => people.Count == 0,
            RetentionOperators.Anyone => people.Count > 0,
            RetentionOperators.Everyone => everyone.Count > 0 && everyone.All(user => people.Contains(user, StringComparer.OrdinalIgnoreCase)),
            "in" => Values(value).Any(user => people.Contains(user, StringComparer.OrdinalIgnoreCase)),
            "notin" => !Values(value).Any(user => people.Contains(user, StringComparer.OrdinalIgnoreCase)),
            _ => false
        };

    internal static bool TextList(string op, string? value, IReadOnlyList<string> names)
        => op switch
        {
            "in" => Values(value).Any(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "all" => Values(value) is { Length: > 0 } wanted && wanted.All(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "notin" => !Values(value).Any(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "set" => names.Count > 0,
            "unset" => names.Count == 0,
            _ => false
        };

    internal static bool Text(string op, string? value, string? text)
        => op switch
        {
            "set" => !string.IsNullOrWhiteSpace(text),
            "unset" => string.IsNullOrWhiteSpace(text),
            "has" => text is not null && text.Contains(value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "nothas" => text is null || !text.Contains(value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "is" => text is not null && Values(value).Any(entry => string.Equals(entry, text, StringComparison.OrdinalIgnoreCase)),
            "isnot" => text is null || !Values(value).Any(entry => string.Equals(entry, text, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };

    /// <param name="unit">What one unit of the entered value is worth: bytes in a gigabyte, or 1.</param>
    internal static bool Number(string op, string? value, double? actual, double unit)
    {
        if (op == "set") return actual is not null;
        if (op == "unset") return actual is null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var entered))
        {
            return false;
        }

        var target = entered * unit;
        return op switch
        {
            "min" => actual >= target,
            "max" => actual <= target,
            "is" => actual is not null && Math.Abs(actual.Value - target) < 0.0001 * Math.Max(1, unit),
            "isnot" => actual is null || Math.Abs(actual.Value - target) >= 0.0001 * Math.Max(1, unit),
            _ => false
        };
    }

    internal static bool Boolean(string? value, bool actual)
        => bool.TryParse(value, out var wanted) && wanted == actual;

    internal bool Date(string op, string? value, DateTimeOffset? when)
    {
        if (op == "set") return when is not null;
        if (op == "unset") return when is null;
        if (when is null) return false;
        if (op is "beyond" or "within" or "next")
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)) return false;
            return op switch
            {
                "beyond" => when < now.AddDays(-days),
                "within" => when >= now.AddDays(-days) && when <= now,
                _ => when >= now && when <= now.AddDays(days)
            };
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var boundary)) return false;
        return op == "after" ? when > boundary : when < boundary;
    }

    /// <summary>The field and its test, ready for ": what it is".</summary>
    private static string Lead(RetentionFieldDefinition field, string op, string? value)
        => Phrase(field, op, value) is { Length: > 0 } phrase ? $"{field.Label} {phrase}" : field.Label;

    /// <summary>The operator and value, as a person reads them.</summary>
    public static string Phrase(RetentionFieldDefinition field, string op, string? value)
    {
        if (op is "set" or "unset" && field.ValueKind == RetentionValueKinds.Date)
        {
            // Followed by the date or "never", which already says it.
            return string.Empty;
        }

        var joined = string.Join(", ", Values(value));
        var unit = field.ValueKind == RetentionValueKinds.Gigabytes ? " GB" : field.Id == "disk.freePercent" ? "%" : field.Id == "server.bitrate" ? " kbps" : string.Empty;
        return op switch
        {
            RetentionOperators.Nobody => "nobody",
            RetentionOperators.Anyone => "anyone",
            RetentionOperators.Everyone => "everyone",
            "in" => $"includes any of {joined}",
            "all" => $"includes all of {joined}",
            "notin" => $"includes none of {joined}",
            "is" when field.ValueKind == RetentionValueKinds.Boolean => bool.TryParse(value, out var yes) && yes ? "is yes" : "is no",
            "is" => $"is {joined}{unit}",
            "isnot" => $"is not {joined}{unit}",
            "min" => $"is at least {joined}{unit}",
            "max" => $"is at most {joined}{unit}",
            "has" => $"contains “{joined}”",
            "nothas" => $"does not contain “{joined}”",
            "before" => $"is before {joined}",
            "after" => $"is after {joined}",
            "within" => $"is within the last {joined} days",
            "next" => $"is within the next {joined} days",
            "beyond" => $"is more than {joined} days ago",
            "set" => field.ValueKind is RetentionValueKinds.TextList ? "is not empty" : "is set",
            "unset" => field.ValueKind is RetentionValueKinds.TextList ? "is empty" : "is not set",
            _ => $"{op} {joined}"
        };
    }
}
