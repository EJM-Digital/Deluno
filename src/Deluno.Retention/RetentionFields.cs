using Deluno.Contracts;
using Deluno.Connections.Contracts;

namespace Deluno.Retention;

/// <summary>What a rule acts on (#494): a whole movie or show, or part of a show.</summary>
public static class RetentionLevels
{
    public const string Movie = "movie";
    public const string Show = "show";
    public const string Season = "season";
    public const string Episode = "episode";

    public static readonly IReadOnlyList<string> All = [Movie, Show, Season, Episode];

    public static string MediaType(string level) => level == Movie ? "movies" : "tv";

    /// <summary>A first-version rule had no level: its media type was it.</summary>
    public static string Normalize(string? level, string? mediaType)
        => level?.Trim().ToLowerInvariant() switch
        {
            Movie or Show or Season or Episode => level.Trim().ToLowerInvariant(),
            _ => mediaType == "tv" ? Show : Movie
        };

    public static bool IsPartOfShow(string level) => level is Season or Episode;
}

/// <summary>Where a field's answer comes from.</summary>
public static class RetentionFieldSources
{
    public const string Deluno = "deluno";
    public const string Server = "server";
    public const string Disk = "disk";
    public const string Seerr = "seerr";
}

/// <summary>The operators the rule fields add to the library filter vocabulary.</summary>
public static class RetentionOperators
{
    // Users: who, on the servers a rule reads.
    public const string Anyone = "anyone";
    public const string Nobody = "nobody";
    public const string Everyone = "everyone";

    public static IReadOnlyList<string> For(string valueKind)
        => valueKind switch
        {
            RetentionValueKinds.Users => [Nobody, Anyone, Everyone, "in", "notin"],
            RetentionValueKinds.TextList => ["in", "all", "notin", "set", "unset"],
            RetentionValueKinds.Text => ["has", "nothas", "is", "isnot", "set", "unset"],
            RetentionValueKinds.Date => ["beyond", "within", "next", "after", "before", "set", "unset"],
            RetentionValueKinds.Boolean => ["is"],
            _ => ["min", "max", "is", "isnot", "set", "unset"]
        };
}

public static class RetentionValueKinds
{
    public const string Users = "users";
    public const string TextList = "textList";
    public const string Text = "text";
    public const string Date = "date";
    public const string Boolean = "boolean";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Rating = "rating";
    public const string Gigabytes = "gigabytes";
}

/// <summary>
/// A field a rule can use that the library filters do not have: what the media
/// servers say, the space on the library's drive, and the parts of a show.
/// </summary>
/// <param name="Levels">The levels the field means something at.</param>
/// <param name="UnsupportedOn">Server kinds that cannot answer it. A rule reading only those is refused.</param>
public sealed record RetentionFieldDefinition(
    string Id,
    string Label,
    string Hint,
    string Group,
    string ValueKind,
    string Source,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string>? UnsupportedOn = null)
{
    public IReadOnlyList<string> Operators => RetentionOperators.For(ValueKind);
}

public static class RetentionFieldCatalog
{
    private static readonly IReadOnlyList<string> Everywhere = RetentionLevels.All;
    private static readonly IReadOnlyList<string> Totals = [RetentionLevels.Show, RetentionLevels.Season];
    private static readonly IReadOnlyList<string> Episodes = [RetentionLevels.Episode];
    private static readonly IReadOnlyList<string> Seasons = [RetentionLevels.Season];
    private static readonly IReadOnlyList<string> Parts = [RetentionLevels.Season, RetentionLevels.Episode];
    private static readonly IReadOnlyList<string> NotPlex = [MediaServerKinds.Plex];
    private static readonly IReadOnlyList<string> PlexOnly = [MediaServerKinds.Jellyfin, MediaServerKinds.Emby];

    private const string Watching = "watching";
    private const string ServerDetails = "server";
    private const string Storage = "storage";
    private const string Part = "part";
    private const string Requests = "requests";

    public static readonly IReadOnlyList<RetentionFieldDefinition> Fields =
    [
        new("server.watchedBy", "Watched by", "Who has finished it. A show or season counts once every episode in it has been.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Everywhere),
        new("server.watchedBySinceAdded", "Watched by, since added", "Who has finished it, counting only plays after it joined Deluno.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Everywhere),
        new("server.timesViewed", "Times viewed", "How many times it has been finished, added up across people.", Watching, RetentionValueKinds.Integer, RetentionFieldSources.Server, Everywhere),
        new("server.playAttempts", "Play attempts (unfinished)", "People who started it and stopped part way.", Watching, RetentionValueKinds.Integer, RetentionFieldSources.Server, Everywhere),
        new("server.lastViewed", "Last viewed (finished)", "The most recent time anyone finished it.", Watching, RetentionValueKinds.Date, RetentionFieldSources.Server, Everywhere),
        new("server.lastPlayed", "Last played (finished or not)", "The most recent time anyone played it at all, whether or not they finished.", Watching, RetentionValueKinds.Date, RetentionFieldSources.Server, Everywhere),
        new("server.favouritedBy", "Favourited by", "Who marked it a favourite.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Everywhere, NotPlex),
        new("server.watchlistedBy", "Watchlisted by", "Who has it on their watchlist.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Everywhere, PlexOnly),
        new("server.userRating", "User rating", "The highest score anyone gave it, out of ten.", Watching, RetentionValueKinds.Rating, RetentionFieldSources.Server, Everywhere),
        new("server.allEpisodesSeenBy", "All episodes seen by", "Who has finished every episode.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Totals),
        new("server.allEpisodesSeenBySinceAdded", "All episodes seen by, since added", "Who has finished every episode, counting only plays after the show joined Deluno.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Totals),
        new("server.watchers", "Watchers", "Who has finished at least one episode.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Totals),
        new("server.watchersSinceAdded", "Watchers, since added", "Who has finished at least one episode, counting only plays after the show joined Deluno.", Watching, RetentionValueKinds.Users, RetentionFieldSources.Server, Totals),
        new("server.watchedEpisodes", "Watched episodes", "Episodes somebody has finished.", Watching, RetentionValueKinds.Integer, RetentionFieldSources.Server, Totals),
        new("server.totalViews", "Total views", "Every finished viewing of every episode, added up.", Watching, RetentionValueKinds.Integer, RetentionFieldSources.Server, Totals),
        new("server.newestEpisodeViewed", "Newest episode view", "The most recent time anyone played an episode.", Watching, RetentionValueKinds.Date, RetentionFieldSources.Server, Totals),

        new("server.added", "Added to the server", "When the server first saw it.", ServerDetails, RetentionValueKinds.Date, RetentionFieldSources.Server, Everywhere),
        new("server.releaseDate", "Release date", "Release date, or an episode's air date, as the server has it.", ServerDetails, RetentionValueKinds.Date, RetentionFieldSources.Server, Everywhere),
        new("server.availableEpisodes", "Available episodes", "Episodes the server holds.", ServerDetails, RetentionValueKinds.Integer, RetentionFieldSources.Server, Totals),
        new("server.lastEpisodeAdded", "Last episode added", "When the newest episode arrived on the server.", ServerDetails, RetentionValueKinds.Date, RetentionFieldSources.Server, Totals),
        new("server.lastEpisodeAired", "Last episode aired", "When the newest episode the server holds went to air.", ServerDetails, RetentionValueKinds.Date, RetentionFieldSources.Server, Totals),
        new("server.ratingCritic", "Critic rating", "Out of ten.", ServerDetails, RetentionValueKinds.Rating, RetentionFieldSources.Server, Everywhere),
        new("server.ratingAudience", "Audience rating", "Out of ten.", ServerDetails, RetentionValueKinds.Rating, RetentionFieldSources.Server, Everywhere),
        new("server.ratingImdb", "IMDb rating", "Out of ten, where the server carries it.", ServerDetails, RetentionValueKinds.Rating, RetentionFieldSources.Server, Everywhere, PlexOnly),
        new("server.ratingRottenTomatoesCritic", "Rotten Tomatoes critic rating", "Out of ten.", ServerDetails, RetentionValueKinds.Rating, RetentionFieldSources.Server, Everywhere),
        new("server.ratingRottenTomatoesAudience", "Rotten Tomatoes audience rating", "Out of ten, where the server carries it.", ServerDetails, RetentionValueKinds.Rating, RetentionFieldSources.Server, Everywhere, PlexOnly),
        new("server.ratingTmdb", "TMDb rating", "Out of ten.", ServerDetails, RetentionValueKinds.Rating, RetentionFieldSources.Server, Everywhere),
        new("server.people", "People involved", "Cast and crew, as the server lists them.", ServerDetails, RetentionValueKinds.TextList, RetentionFieldSources.Server, Everywhere),
        new("server.genres", "Genres", "As the server has them.", ServerDetails, RetentionValueKinds.TextList, RetentionFieldSources.Server, Everywhere),
        new("server.labels", "Labels and tags", "Labels on Plex, tags on Jellyfin and Emby.", ServerDetails, RetentionValueKinds.TextList, RetentionFieldSources.Server, Everywhere),
        new("server.collections", "Collection names", "The collections it is in.", ServerDetails, RetentionValueKinds.TextList, RetentionFieldSources.Server, Everywhere),
        new("server.collectionCount", "Collections present in", "How many collections it is in.", ServerDetails, RetentionValueKinds.Integer, RetentionFieldSources.Server, Everywhere),
        new("server.playlists", "Playlist names", "The playlists it, or an episode of it, is on.", ServerDetails, RetentionValueKinds.TextList, RetentionFieldSources.Server, Everywhere),
        new("server.playlistCount", "Playlist count", "How many playlists it, or an episode of it, is on.", ServerDetails, RetentionValueKinds.Integer, RetentionFieldSources.Server, Everywhere),
        new("server.resolution", "Video resolution", "2160p, 1080p, 720p, 480p or SD, as the server read the file.", ServerDetails, RetentionValueKinds.Text, RetentionFieldSources.Server, [RetentionLevels.Movie, RetentionLevels.Episode]),
        new("server.bitrate", "File bitrate", "Kilobits a second, as the server read the file.", ServerDetails, RetentionValueKinds.Integer, RetentionFieldSources.Server, [RetentionLevels.Movie, RetentionLevels.Episode]),
        new("server.codec", "Video codec", "As the server read the file: h264, hevc, av1.", ServerDetails, RetentionValueKinds.Text, RetentionFieldSources.Server, [RetentionLevels.Movie, RetentionLevels.Episode]),

        new("disk.freeGb", "Free space on the library drive", "Gigabytes free where the library lives. Checked again before anything is removed.", Storage, RetentionValueKinds.Gigabytes, RetentionFieldSources.Disk, Everywhere),
        new("disk.freePercent", "Free space on the library drive (%)", "Percent free where the library lives.", Storage, RetentionValueKinds.Decimal, RetentionFieldSources.Disk, Everywhere),
        new("disk.totalGb", "Size of the library drive", "Gigabytes in total where the library lives.", Storage, RetentionValueKinds.Gigabytes, RetentionFieldSources.Disk, Everywhere),

        new("seerr.requestedBy", "Requested by", "Who asked for it in Seerr. A season or episode is asked about through its show.", Requests, RetentionValueKinds.Users, RetentionFieldSources.Seerr, Everywhere),
        new("seerr.requestDate", "Request date", "When it was most recently requested in Seerr.", Requests, RetentionValueKinds.Date, RetentionFieldSources.Seerr, Everywhere),
        new("seerr.approvalDate", "Approval date", "When a request for it was most recently approved in Seerr.", Requests, RetentionValueKinds.Date, RetentionFieldSources.Seerr, Everywhere),
        new("seerr.isRequested", "Is requested", "Whether anyone has asked for it in Seerr.", Requests, RetentionValueKinds.Boolean, RetentionFieldSources.Seerr, Everywhere),
        new("seerr.timesRequested", "Times requested", "How many requests Seerr holds for it.", Requests, RetentionValueKinds.Integer, RetentionFieldSources.Seerr, Everywhere),

        new("part.seasonNumber", "Season number", "Specials are season 0.", Part, RetentionValueKinds.Integer, RetentionFieldSources.Deluno, Parts),
        new("part.latestSeason", "Latest season", "Whether it is, or is in, the show's newest season.", Part, RetentionValueKinds.Boolean, RetentionFieldSources.Deluno, Parts),
        new("part.hasUnaired", "Has unaired episodes", "Whether the season still has episodes to come.", Part, RetentionValueKinds.Boolean, RetentionFieldSources.Deluno, Parts),
        new("part.size", "Size on disk", "Gigabytes Deluno holds for it.", Part, RetentionValueKinds.Gigabytes, RetentionFieldSources.Deluno, Parts),
        new("part.episodesHeld", "Episodes held", "Episodes of the season Deluno has a file for.", Part, RetentionValueKinds.Integer, RetentionFieldSources.Deluno, Seasons),
        new("part.episodeNumber", "Episode number", "Within its season.", Part, RetentionValueKinds.Integer, RetentionFieldSources.Deluno, Episodes),
        new("part.airDate", "Air date", "When the episode went to air.", Part, RetentionValueKinds.Date, RetentionFieldSources.Deluno, Episodes),
        new("part.imported", "File imported", "When the copy Deluno holds arrived.", Part, RetentionValueKinds.Date, RetentionFieldSources.Deluno, Parts),
        new("part.quality", "Quality", "The tier the episode's file is.", Part, RetentionValueKinds.Text, RetentionFieldSources.Deluno, Episodes),
        new("part.qualityRank", "Quality (number)", "The same tier, as a number, so a rule can compare thresholds rather than just names.", Part, RetentionValueKinds.Integer, RetentionFieldSources.Deluno, Episodes),
        new("part.cutoffMet", "Quality cutoff met", "Whether Deluno has stopped looking for a better copy.", Part, RetentionValueKinds.Boolean, RetentionFieldSources.Deluno, Episodes),
        new("part.monitored", "Monitored", "Whether Deluno is watching for it.", Part, RetentionValueKinds.Boolean, RetentionFieldSources.Deluno, Episodes),
        new("part.path", "File path", "Where the episode's file sits.", Part, RetentionValueKinds.Text, RetentionFieldSources.Deluno, Episodes)
    ];

    public static RetentionFieldDefinition? Find(string id)
        => Fields.FirstOrDefault(field => string.Equals(field.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsRetentionField(string condition)
    {
        var field = condition.Split(':', 2)[0];
        return field.StartsWith("server.", StringComparison.OrdinalIgnoreCase) ||
               field.StartsWith("disk.", StringComparison.OrdinalIgnoreCase) ||
               field.StartsWith("part.", StringComparison.OrdinalIgnoreCase) ||
               field.StartsWith("seerr.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A catalogue field a whole show carries, read at a season or episode through its show.</summary>
    public static MediaKind CatalogueKind(string level) => level == RetentionLevels.Movie ? MediaKind.Movie : MediaKind.Series;

    /// <summary>
    /// The first version's two watch conditions, in this vocabulary: <c>watched:is:true</c>
    /// is "watched by anyone", <c>lastPlayed:beyond:30</c> is "last viewed more than 30 days ago".
    /// </summary>
    public static string Upgrade(string condition)
    {
        var parts = condition.Split(':', 3);
        if (string.Equals(parts[0], "watched", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
        {
            return bool.TryParse(parts[2], out var watched) && watched ? "server.watchedBy:anyone:" : "server.watchedBy:nobody:";
        }

        if (string.Equals(parts[0], "lastPlayed", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
        {
            return $"server.lastViewed:{parts[1]}:{(parts.Length > 2 ? parts[2] : string.Empty)}";
        }

        return condition;
    }
}
