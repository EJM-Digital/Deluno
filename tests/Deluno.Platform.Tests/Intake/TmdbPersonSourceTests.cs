using Deluno.Intake;

namespace Deluno.Platform.Tests.Intake;

public sealed class TmdbPersonSourceTests
{
    [Fact]
    public void Parses_a_person_url_and_defaults_to_cast()
    {
        Assert.True(TmdbPersonSource.TryParse(
            "https://www.themoviedb.org/person/6384",
            out var personId,
            out var creditTypes));

        Assert.Equal("6384", personId);
        Assert.Equal(["cast"], creditTypes);
    }

    [Fact]
    public void Parses_and_normalizes_the_selected_credit_types()
    {
        Assert.True(TmdbPersonSource.TryParse(
            "https://www.themoviedb.org/person/6384?credits=CAST%2CDirector%2Cunknown%2Csound",
            out _,
            out var creditTypes));

        Assert.Equal(["cast", "director", "sound"], creditTypes);
    }

    [Fact]
    public void Builds_the_migration_free_address_from_a_person_id()
    {
        Assert.Equal(
            "https://www.themoviedb.org/person/6384?credits=cast%2Cdirector%2Cwriting",
            TmdbPersonSource.BuildAddress("6384", ["cast", "director", "writing"]));
    }

    [Fact]
    public void Rejects_a_title_url_as_a_person_address()
    {
        Assert.False(TmdbPersonSource.TryParse(
            "https://www.themoviedb.org/movie/603",
            out _,
            out _));
    }
}
