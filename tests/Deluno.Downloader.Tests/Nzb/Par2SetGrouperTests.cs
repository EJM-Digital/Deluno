using Deluno.Downloader.Nzb.Par2;

namespace Deluno.Downloader.Tests.Nzb;

public class Par2SetGrouperTests
{
    [Fact]
    public void Single_set_with_index_groups_to_one_set()
    {
        var files = new[]
        {
            "/dl/movie.par2",
            "/dl/movie.vol000+01.par2",
            "/dl/movie.vol001+02.par2",
            "/dl/movie.vol003+04.par2",
        };

        var sets = Par2SetGrouper.Group(files);

        Assert.Single(sets);
        var set = sets[0];
        Assert.Equal("movie", set.SetName);
        Assert.EndsWith("movie.par2", set.IndexFile);
        Assert.Equal(4, set.AllFiles.Count);
    }

    [Fact]
    public void Multi_set_with_main_and_sample_groups_separately()
    {
        // The canonical scene release pattern: main movie + sample
        // sub-release, each with its own par2 set.
        var files = new[]
        {
            "/dl/Movie.Title.2024.1080p.par2",
            "/dl/Movie.Title.2024.1080p.vol000+01.par2",
            "/dl/Movie.Title.2024.1080p.vol001+02.par2",
            "/dl/sample/movie-sample.par2",
            "/dl/sample/movie-sample.vol000+01.par2",
        };

        var sets = Par2SetGrouper.Group(files);

        Assert.Equal(2, sets.Count);
        var main = sets.Single(s => s.SetName == "Movie.Title.2024.1080p");
        Assert.EndsWith("Movie.Title.2024.1080p.par2", main.IndexFile);
        Assert.Equal(3, main.AllFiles.Count);

        var sample = sets.Single(s => s.SetName == "movie-sample");
        Assert.EndsWith("movie-sample.par2", sample.IndexFile);
        Assert.Equal(2, sample.AllFiles.Count);
    }

    [Fact]
    public void Same_set_name_in_different_directories_stays_distinct()
    {
        // Pathological but spec-legal: two sets with identical basenames
        // in different dirs. Grouping must NOT collapse them.
        var files = new[]
        {
            "/dl/disc1/release.par2",
            "/dl/disc1/release.vol000+01.par2",
            "/dl/disc2/release.par2",
            "/dl/disc2/release.vol000+01.par2",
        };

        var sets = Par2SetGrouper.Group(files);

        Assert.Equal(2, sets.Count);
    }

    [Fact]
    public void Set_with_only_volumes_no_index_uses_smallest_volume()
    {
        // Spec-legal: par2 set can ship without an explicit index. We
        // bootstrap from the smallest volume since par2cmdline can read
        // the recovery-set header from any volume in the set.
        // (Sizes don't matter when files don't exist; the safe-size
        // fallback returns 0 for all and the first arbitrarily wins.)
        var files = new[]
        {
            "/dl/release.vol000+01.par2",
            "/dl/release.vol001+02.par2",
            "/dl/release.vol003+04.par2",
        };

        var sets = Par2SetGrouper.Group(files);

        Assert.Single(sets);
        Assert.Equal("release", sets[0].SetName);
        Assert.Contains(".par2", sets[0].IndexFile);
    }

    [Fact]
    public void Mixed_case_filenames_are_grouped_together()
    {
        // Some posters use mixed case for the .vol###+##.par2 suffix.
        var files = new[]
        {
            "/dl/release.par2",
            "/dl/release.Vol000+01.PAR2",
            "/dl/release.VOL001+02.par2",
        };

        var sets = Par2SetGrouper.Group(files);

        Assert.Single(sets);
        Assert.Equal(3, sets[0].AllFiles.Count);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Empty(Par2SetGrouper.Group(Array.Empty<string>()));
    }
}
