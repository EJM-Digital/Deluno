using System.Globalization;
using System.Text;
using Deluno.Movies.Contracts;
using Deluno.Series.Contracts;

namespace Deluno.Api.Calendar;

/// <summary>
/// Renders the schedule as an iCalendar document (RFC 5545) so a phone or
/// desktop calendar can subscribe to it, the way Sonarr and Radarr feeds are
/// used. Episodes become timed events on their air time; movie release dates
/// have no time of day, so they become all-day events.
/// </summary>
public static class CalendarFeedBuilder
{
    private const string ProductId = "-//Deluno//Media Calendar//EN";

    /// <summary>iCalendar lines are limited to 75 octets; longer ones fold onto continuation lines.</summary>
    private const int MaxLineOctets = 74;

    public static string Build(
        IReadOnlyList<SeriesCalendarEpisodeItem> episodes,
        IReadOnlyList<MovieCalendarItem> movies,
        DateTimeOffset generatedUtc,
        string instanceName)
    {
        var builder = new StringBuilder();
        AppendLine(builder, "BEGIN:VCALENDAR");
        AppendLine(builder, "VERSION:2.0");
        AppendLine(builder, $"PRODID:{ProductId}");
        AppendLine(builder, "CALSCALE:GREGORIAN");
        AppendLine(builder, "METHOD:PUBLISH");
        AppendLine(builder, $"X-WR-CALNAME:{Escape(instanceName)}");

        var stamp = Timestamp(generatedUtc);

        foreach (var episode in episodes)
        {
            var code = $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            var name = string.IsNullOrWhiteSpace(episode.Title)
                ? $"{episode.SeriesTitle} — {code}"
                : $"{episode.SeriesTitle} — {code} — {episode.Title}";

            AppendLine(builder, "BEGIN:VEVENT");
            AppendLine(builder, $"UID:episode-{episode.EpisodeId}@deluno");
            AppendLine(builder, $"DTSTAMP:{stamp}");
            AppendLine(builder, $"DTSTART:{Timestamp(episode.AirDateUtc)}");
            // Deluno does not know an episode's runtime, so every episode is
            // given the same nominal half hour rather than an invented length.
            AppendLine(builder, $"DTEND:{Timestamp(episode.AirDateUtc.AddMinutes(30))}");
            AppendLine(builder, $"SUMMARY:{Escape(name)}");
            AppendLine(builder, $"DESCRIPTION:{Escape(DescribeEpisode(episode))}");
            AppendLine(builder, $"CATEGORIES:{Escape("TV")}");
            AppendLine(builder, "END:VEVENT");
        }

        foreach (var movie in movies)
        {
            var name = movie.ReleaseYear is null
                ? $"{movie.Title} — {DescribeMovieKind(movie.Kind)}"
                : $"{movie.Title} ({movie.ReleaseYear}) — {DescribeMovieKind(movie.Kind)}";

            AppendLine(builder, "BEGIN:VEVENT");
            AppendLine(builder, $"UID:movie-{movie.MovieId}-{movie.Kind}@deluno");
            AppendLine(builder, $"DTSTAMP:{stamp}");
            // A release date is a day, not a moment: an all-day event avoids
            // pinning it to midnight in whichever timezone the reader is in.
            AppendLine(builder, $"DTSTART;VALUE=DATE:{movie.Date:yyyyMMdd}");
            AppendLine(builder, $"DTEND;VALUE=DATE:{movie.Date.AddDays(1):yyyyMMdd}");
            AppendLine(builder, $"SUMMARY:{Escape(name)}");
            AppendLine(builder, $"DESCRIPTION:{Escape(DescribeMovie(movie))}");
            AppendLine(builder, $"CATEGORIES:{Escape("Movies")}");
            AppendLine(builder, "END:VEVENT");
        }

        AppendLine(builder, "END:VCALENDAR");
        return builder.ToString();
    }

    private static string DescribeEpisode(SeriesCalendarEpisodeItem episode)
        => episode.HasFile
            ? "In your library."
            : episode.Monitored
                ? "Deluno is watching for this episode."
                : "Not monitored.";

    private static string DescribeMovie(MovieCalendarItem movie)
        => movie.HasFile
            ? "In your library."
            : movie.Monitored
                ? "Deluno is watching for this movie."
                : "Not monitored.";

    private static string DescribeMovieKind(string kind) => kind.ToLowerInvariant() switch
    {
        "incinemas" => "In cinemas",
        "digital" => "Digital release",
        "physical" => "Physical release",
        _ => "Release"
    };

    private static string Timestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>RFC 5545 §3.3.11: backslash, semicolon, comma and newline are escaped in text values.</summary>
    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Appends one content line, folded to the octet limit. Folding counts
    /// UTF-8 octets rather than characters, and never splits one character
    /// across the fold — an episode title with an accent or an em dash would
    /// otherwise produce a corrupt line.
    /// </summary>
    private static void AppendLine(StringBuilder builder, string line)
    {
        var bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes <= MaxLineOctets)
        {
            builder.Append(line).Append("\r\n");
            return;
        }

        var remaining = line.AsSpan();
        var first = true;
        while (!remaining.IsEmpty)
        {
            // A continuation line begins with one space, so it can carry one
            // octet less of payload than the first line.
            var budget = first ? MaxLineOctets : MaxLineOctets - 1;
            var take = TakeByOctets(remaining, budget);
            if (!first)
            {
                builder.Append(' ');
            }

            builder.Append(remaining[..take]).Append("\r\n");
            remaining = remaining[take..];
            first = false;
        }
    }

    private static int TakeByOctets(ReadOnlySpan<char> value, int budget)
    {
        var octets = 0;
        var index = 0;
        while (index < value.Length)
        {
            // Keep a surrogate pair together: it is one character to the reader.
            var step = char.IsHighSurrogate(value[index]) && index + 1 < value.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(value.Slice(index, step));
            if (octets + size > budget)
            {
                break;
            }

            octets += size;
            index += step;
        }

        // A single character wider than the budget would otherwise loop forever.
        return index == 0 ? Math.Min(value.Length, 1) : index;
    }
}
