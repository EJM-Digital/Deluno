using Deluno.Worker.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Deluno.Worker.Tests.Properties;

/// <summary>#1271: the year read out of a release name, as properties over generated names.</summary>
public sealed class ReleaseNameYearProperties
{
    private static readonly string[] Separators = [".", " ", "_", "-"];
    private static readonly string[] Words = ["Night", "Of", "The", "Living", "Dead", "Blade", "Runner", "Kid", "House", "Hill", "Alien"];

    private sealed record Name(string[] Title, int Year, string Separator);

    private static Arbitrary<Name> Names()
        => (from words in Gen.Elements(Words).ArrayOf()
            from year in Gen.Choose(1900, 2100)
            from separator in Gen.Elements(Separators)
            select new Name(words.Length == 0 ? ["Alien"] : words, year, separator)).ToArbitrary();

    [Fact]
    public void A_year_written_after_a_title_is_the_year_read_whatever_separator_joins_the_words()
        => Prop.ForAll(Names(), name =>
        {
            var text = string.Join(name.Separator, name.Title) + name.Separator + name.Year + name.Separator + "1080p" + name.Separator + "BluRay-GRP";
            return ReleaseNameParser.InferYear(text) == name.Year;
        }).QuickCheckThrowOnFailure();

    [Property]
    public bool InferYear_never_throws_and_any_year_it_returns_is_between_1900_and_2100(string? text)
    {
        var year = ReleaseNameParser.InferYear(text!);
        return year is null or >= 1900 and <= 2100;
    }

    [Fact]
    public void The_last_year_wins_so_a_title_that_is_itself_a_year_is_not_the_release_year()
        => Prop.ForAll((from a in Gen.Choose(1900, 2100) from b in Gen.Choose(1900, 2100) select (a, b)).ToArbitrary(), pair =>
            ReleaseNameParser.InferYear($"Blade.Runner.{pair.Item1}.{pair.Item2}.1080p") == pair.Item2).QuickCheckThrowOnFailure();

    [Fact]
    public void A_name_with_no_four_digit_token_has_no_year()
        => Prop.ForAll(Gen.Elements(Words).ArrayOf().ToArbitrary(), words =>
            ReleaseNameParser.InferYear(string.Join(".", words) + ".1080p.x264") is null).QuickCheckThrowOnFailure();
}
