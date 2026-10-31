using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>Every filter a tracker definition can name, and the number and size readers beside them. #1268.</summary>
public sealed class TrackerFiltersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TrackerContext Context = new(Now);
    private static readonly Dictionary<string, object?> Variables = new() { [".Keywords"] = "World" };

    private static string Run(string value, string filter, params string[] args)
        => TrackerFilters.Apply(value, [new TrackerFilter(filter, args)], Variables, Context);

    [Fact]
    public void A_query_string_value_is_found_decoded_and_empty_when_missing()
    {
        Assert.Equal("2", Run("get.php?x=1&y=2#frag", "querystring", "y"));
        Assert.Equal("1", Run("get.php?x=1&y=2", "querystring", "x"));
        Assert.Equal("5", Run("id=5", "querystring", "id"));
        Assert.Equal("c/d", Run("?a%20b=c%2Fd", "QueryString", "a b"));
        Assert.Equal(string.Empty, Run("?flag", "querystring", "flag"));
        Assert.Equal(string.Empty, Run("?x=1", "querystring", "missing"));
        Assert.Equal(string.Empty, Run("?x=1", "querystring"));
        Assert.Null(TrackerFilters.QueryValue("?x=1", "y"));
    }

    [Fact]
    public void Dates_are_read_by_layout_or_by_how_long_ago_or_left_as_they_were()
    {
        Assert.Equal(
            TrackerDates.Format(new DateTimeOffset(2026, 8, 24, 10, 15, 0, TimeSpan.FromHours(1))),
            Run("2026-08-24 10:15:00 +01:00", "dateparse", "yyyy-MM-dd HH:mm:ss zzz"));
        Assert.Equal("not a date", Run("not a date", "timeparse", "yyyy-MM-dd"));
        Assert.Equal(TrackerDates.Format(TrackerDates.FromTimeAgo("2 hours ago", Now)), Run("2 hours ago", "timeago"));
        Assert.Equal(TrackerDates.Format(TrackerDates.FromTimeAgo("3 days ago", Now)), Run("3 days ago", "reltime"));
        Assert.Equal(TrackerDates.Format(TrackerDates.FromUnknown("yesterday", Now)), Run("yesterday", "fuzzytime"));
    }

    [Fact]
    public void Text_is_matched_replaced_split_trimmed_and_joined()
    {
        Assert.Equal("123", Run("id 123 here", "regexp", @"(\d+)"));
        Assert.Equal(string.Empty, Run("no digits", "regexp", @"(\d+)"));
        Assert.Equal(string.Empty, Run("abc", "regexp", "abc"));
        Assert.Equal("abc#", Run("abc123", "re_replace", @"\d+", "#"));
        Assert.Equal("b", Run("a,b,c", "split", ",", "1"));
        Assert.Equal("c", Run("a,b,c", "split", ",", "-1"));
        Assert.Equal("y", Run("x y z", "split", "", "1"));
        Assert.Throws<FormatException>(() => Run("a,b", "split", ",", "5"));
        Assert.Throws<FormatException>(() => Run("a,b", "split", ",", "-5"));
        Assert.Equal("a-b", Run("a b", "replace", " ", "-"));
        Assert.Equal("a b", Run("a b", "replace", ""));
        Assert.Equal("xx", Run("xx", "replace"));
        Assert.Equal("a", Run("  a  ", "trim"));
        Assert.Equal("a", Run("--a--", "trim", "-"));
        Assert.Equal("a", Run("  a  ", "trim", ""));
        Assert.Equal("Hello, x", Run("x", "prepend", "Hello, "));
        Assert.Equal("x World", Run("x", "append", " {{ .Keywords }}"));
    }

    [Fact]
    public void Text_is_cased_encoded_cleaned_and_validated()
    {
        Assert.Equal("abc", Run("AbC", "tolower"));
        Assert.Equal("ABC", Run("AbC", "toupper"));
        Assert.Equal("a b&c", Run("a%20b%26c", "urldecode"));
        Assert.Equal("a+b%26c", Run("a b&c", "urlencode"));
        Assert.Equal("<b>&", Run("&lt;b&gt;&amp;", "htmldecode"));
        Assert.Equal("&lt;b&gt;", Run("<b>", "htmlencode"));
        Assert.Equal("a_b", Run("a/b", "validfilename"));
        Assert.Equal("Cafe Nandu", Run("Café Ñandú", "diacritics").Replace("ú", "u"));
        Assert.Equal("comedy,drama", Run("Action, Comedy; Drama", "validate", "Comedy,Drama,Horror"));
        Assert.Equal("a|b", Run("""{"t":["a","b"]}""", "jsonjoinarray", "t", "|"));
        Assert.Equal("same", Run("same", "hexdump"));
        Assert.Equal("same", Run("same", "strdump"));
        Assert.Throws<NotSupportedException>(() => Run("x", "nonsense"));
    }

    [Fact]
    public void A_title_matches_when_it_has_every_word_ignoring_case_marks_and_extra_length()
    {
        Assert.True(TrackerFilters.MatchesAllWords("Anything", null));
        Assert.True(TrackerFilters.MatchesAllWords("Anything", "  "));
        Assert.True(TrackerFilters.MatchesAllWords("Café Society 2016", "cafe society"));
        Assert.True(TrackerFilters.MatchesAllWords("Dune 2021", "dune 2021 extra", characterLimit: 9));
        Assert.False(TrackerFilters.MatchesAllWords("Dune 2021", "dune 2021 extra"));
        Assert.True(TrackerFilters.MatchesAllWords("Dune 2021", "dune", characterLimit: 20));
        Assert.False(TrackerFilters.MatchesAllWords("Dune", "arrival"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("none", null)]
    [InlineData(".", null)]
    [InlineData("12", 12d)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("1,5", 1.5)]
    [InlineData("1,234", 1234d)]
    [InlineData("1,234,567", 1234567d)]
    [InlineData("1.2.3", 12.3)]
    public void A_number_is_read_whichever_way_it_is_punctuated(string? text, double? expected)
        => Assert.Equal(expected, TrackerNumbers.ParseDouble(text));

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("x", null)]
    [InlineData("-", 0)]
    [InlineData("1,234 files", 1234)]
    [InlineData("99999999999", int.MaxValue)]
    public void A_count_is_read_from_the_digits_in_it(string? text, int? expected)
        => Assert.Equal(expected, TrackerNumbers.ParseInt(text));

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("no digits here", null)]
    [InlineData("7", 7L)]
    [InlineData("3 bytes", 3L)]
    [InlineData("300 kb", 307200L)]
    [InlineData("3 Ko", 3072L)]
    [InlineData("5 MiB", 5242880L)]
    [InlineData("970.3 MB", 1017433292L)]
    [InlineData("1,5 GiB", 1610612736L)]
    [InlineData("5 Go", 5368709120L)]
    [InlineData("1 GB", 1073741824L)]
    [InlineData("2 TB", 2199023255552L)]
    [InlineData("1 TiB", 1099511627776L)]
    [InlineData("1 гб", 1073741824L)]
    [InlineData("2 мб", 2097152L)]
    [InlineData("2 кб", 2048L)]
    [InlineData("1 тб", 1099511627776L)]
    [InlineData("1 t", 1099511627776L)]
    [InlineData("1 g", 1073741824L)]
    [InlineData("1 m", 1048576L)]
    [InlineData("1 k", 1024L)]
    public void A_size_is_read_with_its_unit(string? text, long? expected)
        => Assert.Equal(expected, TrackerNumbers.ParseSize(text));

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("nothing", null)]
    [InlineData("tt0000000", null)]
    [InlineData("tt1234567", "tt1234567")]
    [InlineData("see imdb 12345 now", "tt0012345")]
    public void An_imdb_id_is_found_in_text_and_padded(string? text, string? expected)
        => Assert.Equal(expected, TrackerNumbers.ParseImdbId(text));
}
