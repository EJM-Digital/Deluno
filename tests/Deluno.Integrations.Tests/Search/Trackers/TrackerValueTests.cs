using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#606: sizes, counts, dates and filters as trackers print and definitions transform them.</summary>
public sealed class TrackerValueTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TrackerContext Context = new(Now);
    private static readonly Dictionary<string, object?> NoVariables = new();

    [Theory]
    [InlineData("1.5 GB", 1610612736L)]
    [InlineData("970.29 MB", 1017422807L)]
    [InlineData("大小：970.29 MB", 1017422807L)]
    [InlineData("1,234.5 MiB", 1294467072L)]
    [InlineData("3,2 Go", 3435973836L)]
    [InlineData("700 KB", 716800L)]
    [InlineData("10575236092", 10575236092L)]
    [InlineData("4.1 ГБ", 4402341478L)]
    public void Reads_a_size_in_bytes(string text, long expected)
        => Assert.Equal(expected, TrackerNumbers.ParseSize(text));

    [Theory]
    [InlineData("1,234", 1234)]
    [InlineData(" 49 ", 49)]
    [InlineData("-", 0)]
    public void Reads_a_count(string text, int expected)
        => Assert.Equal(expected, TrackerNumbers.ParseInt(text));

    [Theory]
    [InlineData("tt0063350", "tt0063350")]
    [InlineData("https://www.imdb.com/title/tt0063350/", "tt0063350")]
    [InlineData("63350", "tt0063350")]
    public void Reads_an_imdb_id(string text, string expected)
        => Assert.Equal(expected, TrackerNumbers.ParseImdbId(text));

    [Fact]
    public void Reads_a_date_by_the_definitions_layout()
    {
        var value = TrackerFilters.Apply("2026-08-24 10:15:00", [new TrackerFilter("append", [" +01:00"]), new TrackerFilter("dateparse", ["yyyy-MM-dd HH:mm:ss zzz"])], NoVariables, Context);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 9, 15, 0, TimeSpan.Zero), TrackerDates.FromUnknown(value, Now).ToUniversalTime());
    }

    [Theory]
    [InlineData("3 hours ago", -3 * 60)]
    [InlineData("1 day, 2 hours ago", -(26 * 60))]
    [InlineData("Yesterday 10:00", -26 * 60)]
    [InlineData("just now", 0)]
    [InlineData("1727000000", null)]
    public void Reads_the_dates_trackers_print(string text, int? minutesFromNow)
    {
        var date = TrackerDates.FromUnknown(text, Now);
        if (minutesFromNow is { } minutes)
        {
            Assert.Equal(Now.AddMinutes(minutes), date);
        }
        else
        {
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1727000000), date);
        }
    }

    [Fact]
    public void Runs_filters_in_order()
    {
        var filters = new[]
        {
            new TrackerFilter("regexp", [@"/torrent/\d+/(.+?)/"]),
            new TrackerFilter("replace", ["-", " "]),
            new TrackerFilter("append", [" 1968"]),
            new TrackerFilter("toupper", [])
        };

        Assert.Equal("NIGHT OF THE LIVING DEAD 1968", TrackerFilters.Apply("/torrent/123/night-of-the-living-dead/", filters, NoVariables, Context));
    }

    [Fact]
    public void Reads_a_query_string_parameter()
        => Assert.Equal("42", TrackerFilters.Apply("download.php?id=42&name=x", [new TrackerFilter("querystring", ["id"])], NoVariables, Context));

    [Fact]
    public void A_split_past_the_end_is_an_error_not_a_blank()
        => Assert.Throws<FormatException>(() => TrackerFilters.Apply("a/b", [new TrackerFilter("split", ["/", "5"])], NoVariables, Context));

    [Fact]
    public void Andmatch_wants_every_word()
    {
        Assert.True(TrackerFilters.MatchesAllWords("Night.of.the.Living.Dead.1968.1080p", "night of the living dead"));
        Assert.False(TrackerFilters.MatchesAllWords("Dawn.of.the.Dead.1978", "night of the living dead"));
    }
}
