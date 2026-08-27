using System.Text;
using Deluno.Api.Calendar;
using Deluno.Movies.Contracts;
using Deluno.Series.Contracts;

namespace Deluno.Platform.Tests.Calendar;

public sealed class CalendarFeedBuilderTests
{
    private static readonly DateTimeOffset Generated = DateTimeOffset.Parse("2026-08-25T09:00:00Z");

    private static SeriesCalendarEpisodeItem Episode(
        string title = "Pilot",
        int season = 1,
        int episode = 1,
        bool hasFile = false,
        bool monitored = true)
        => new(
            EpisodeId: "episode-1",
            SeriesId: "series-1",
            SeriesTitle: "Breaking Bad",
            PosterUrl: null,
            SeasonNumber: season,
            EpisodeNumber: episode,
            Title: title,
            AirDateUtc: DateTimeOffset.Parse("2026-09-01T20:30:00Z"),
            HasFile: hasFile,
            Monitored: monitored,
            WantedStatus: "missing");

    private static MovieCalendarItem Movie(string kind = "digital")
        => new(
            MovieId: "movie-1",
            Title: "Blade Runner 2049",
            ReleaseYear: 2017,
            PosterUrl: null,
            Kind: kind,
            Date: new DateOnly(2026, 9, 5),
            HasFile: false,
            Monitored: true,
            WantedStatus: "missing");

    private static IReadOnlyList<string> Lines(string feed)
        => feed.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Build_wraps_events_in_a_valid_calendar_envelope()
    {
        var feed = CalendarFeedBuilder.Build([Episode()], [Movie()], Generated, "Deluno");
        var lines = Lines(feed);

        Assert.Equal("BEGIN:VCALENDAR", lines[0]);
        Assert.Equal("END:VCALENDAR", lines[^1]);
        Assert.Contains("VERSION:2.0", lines);
        Assert.Equal(2, lines.Count(line => line == "BEGIN:VEVENT"));
        Assert.Equal(2, lines.Count(line => line == "END:VEVENT"));
        // Every line ends CRLF per RFC 5545.
        Assert.EndsWith("\r\n", feed);
    }

    [Fact]
    public void Build_gives_an_episode_a_timed_event_and_a_release_an_all_day_event()
    {
        var lines = Lines(CalendarFeedBuilder.Build([Episode()], [Movie()], Generated, "Deluno"));

        Assert.Contains("DTSTART:20260901T203000Z", lines);
        Assert.Contains("DTEND:20260901T210000Z", lines);
        // A release date is a day, not a moment.
        Assert.Contains("DTSTART;VALUE=DATE:20260905", lines);
        Assert.Contains("DTEND;VALUE=DATE:20260906", lines);
    }

    [Fact]
    public void Build_gives_every_event_a_stable_unique_id()
    {
        var lines = Lines(CalendarFeedBuilder.Build([Episode()], [Movie()], Generated, "Deluno"));
        var ids = lines.Where(line => line.StartsWith("UID:", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Contains("UID:episode-episode-1@deluno", ids);
        // A film can appear on several dates, so its kind is part of the id.
        Assert.Contains("UID:movie-movie-1-digital@deluno", ids);
    }

    [Fact]
    public void Build_escapes_characters_that_would_break_a_content_line()
    {
        var lines = Lines(CalendarFeedBuilder.Build(
            [Episode(title: "Semi; colon, comma\\ and a\nnewline")],
            [],
            Generated,
            "Deluno"));

        var summary = Assert.Single(lines, line => line.StartsWith("SUMMARY:", StringComparison.Ordinal));
        Assert.Contains("\\;", summary);
        Assert.Contains("\\,", summary);
        Assert.Contains("\\\\", summary);
        Assert.Contains("\\n", summary);
        // A raw newline would end the content line and corrupt the feed.
        Assert.DoesNotContain('\n', summary);
    }

    [Fact]
    public void Build_folds_long_lines_within_the_octet_limit()
    {
        var lines = Lines(CalendarFeedBuilder.Build(
            [Episode(title: new string('A', 400))],
            [],
            Generated,
            "Deluno"));

        Assert.All(lines, line => Assert.True(
            Encoding.UTF8.GetByteCount(line) <= 75,
            $"Line exceeds the 75-octet limit: {Encoding.UTF8.GetByteCount(line)}"));
        // A folded line continues with a single leading space.
        Assert.Contains(lines, line => line.StartsWith(' '));
    }

    [Fact]
    public void Build_never_splits_a_multi_byte_character_across_a_fold()
    {
        // Em dashes are three octets each: folding by characters rather than
        // octets would either overflow the limit or cut one in half.
        var feed = CalendarFeedBuilder.Build(
            [Episode(title: string.Concat(Enumerable.Repeat("— ", 120)))],
            [],
            Generated,
            "Deluno");

        Assert.All(Lines(feed), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75));
        // Unfolding restores the original text exactly.
        var unfolded = feed.Replace("\r\n ", "", StringComparison.Ordinal);
        Assert.Contains(string.Concat(Enumerable.Repeat("— ", 120)).Trim(), unfolded);
    }

    [Fact]
    public void Build_describes_what_Deluno_will_do_about_each_entry()
    {
        var lines = Lines(CalendarFeedBuilder.Build(
            [Episode(hasFile: true), Episode(monitored: false)],
            [],
            Generated,
            "Deluno"));

        Assert.Contains("DESCRIPTION:In your library.", lines);
        Assert.Contains("DESCRIPTION:Not monitored.", lines);
    }

    [Fact]
    public void Build_names_the_calendar_after_the_installation()
    {
        var lines = Lines(CalendarFeedBuilder.Build([], [], Generated, "Loungeroom"));
        Assert.Contains("X-WR-CALNAME:Loungeroom", lines);
    }

    [Fact]
    public void Build_produces_an_empty_but_valid_calendar_when_nothing_is_scheduled()
    {
        var lines = Lines(CalendarFeedBuilder.Build([], [], Generated, "Deluno"));

        Assert.Equal("BEGIN:VCALENDAR", lines[0]);
        Assert.Equal("END:VCALENDAR", lines[^1]);
        Assert.DoesNotContain("BEGIN:VEVENT", lines);
    }
}
