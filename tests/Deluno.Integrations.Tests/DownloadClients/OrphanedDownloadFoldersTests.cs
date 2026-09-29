using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #588: an entry in a library's completed-download folder that nothing
/// claims is noticed, and Deluno can say whether it downloaded it.
///
/// <para><b>Why this file exists.</b> On the soak rig
/// <c>Downloads-Complete\TV\Breaking.Bad.S01E01…DELUNO.1</c>, a duplicate the
/// client saved under a taken name, had sat there since 15 September with
/// nothing counting it.</para>
/// </summary>
public sealed class OrphanedDownloadFoldersTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T14:00:00Z");
    private const string Tv = @"C:\Deluno\Downloads-Complete\TV";
    private const string Grabbed = "Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO";

    [Fact]
    public void An_unclaimed_duplicate_of_a_grab_is_an_orphan_Deluno_downloaded()
    {
        var found = Find([Entry($@"{Tv}\{Grabbed}.1")], claimed: []);

        var orphan = Assert.Single(found);
        Assert.Equal($"{Grabbed}.1", orphan.Name);
        Assert.True(orphan.DelunoGrabbedIt);
        Assert.Equal("TV", orphan.LibraryName);
    }

    [Fact]
    public void Anything_a_client_import_or_hand_off_is_using_is_not_an_orphan()
    {
        var found = Find(
            [Entry($@"{Tv}\Seeding.Show.S01E01"), Entry($@"{Tv}\Importing.Show.S01E02"), Entry($@"{Tv}\Weir.Show.S01E03")],
            claimed:
            [
                $@"{Tv}\Seeding.Show.S01E01",
                $@"{Tv}\Importing.Show.S01E02\Importing.Show.S01E02.mkv",
                $@"{Tv.Replace('\\', '/')}/Weir.Show.S01E03/"
            ]);

        Assert.Empty(found);
    }

    [Fact]
    public void The_download_folder_itself_or_a_folder_holding_it_claims_nothing_in_it()
    {
        var found = Find([Entry($@"{Tv}\Left.Behind.S01E01")], claimed: [Tv, @"C:\Deluno"]);

        var orphan = Assert.Single(found);
        Assert.False(orphan.DelunoGrabbedIt);
    }

    [Fact]
    public void A_new_entry_or_one_a_person_kept_is_left_alone()
    {
        Assert.Empty(Find([Entry($@"{Tv}\Just.Finished.S01E01", age: TimeSpan.FromMinutes(20))], claimed: []));
        Assert.Empty(Find([Entry($@"{Tv}\Kept.S01E01")], claimed: [], kept: [$@"{Tv}\Kept.S01E01"]));
    }

    [Fact]
    public void A_release_whose_own_name_ends_in_a_number_is_still_recognised()
    {
        var grabbed = new HashSet<string>(["Movie.2020.1080p.WEB-DL.DDP5.1"], StringComparer.OrdinalIgnoreCase);

        Assert.True(OrphanedDownloads.WasGrabbed("Movie.2020.1080p.WEB-DL.DDP5.1", grabbed));
        Assert.True(OrphanedDownloads.WasGrabbed("Movie.2020.1080p.WEB-DL.DDP5.1.2", grabbed));
        Assert.False(OrphanedDownloads.WasGrabbed("Other.2020.1080p", grabbed));
    }

    /// <summary>#1001: names on the skip list are never counted, however old.</summary>
    [Fact]
    public void A_name_on_the_skip_list_is_never_counted_as_a_leftover()
    {
        var entries = new[]
        {
            Entry($@"{Tv}\Show.S01E01.nfo"),
            Entry($@"{Tv}\THUMBS.DB"),
            Entry($@"{Tv}\Left.Behind.S01E01")
        };

        var found = OrphanedDownloads.Find(
            [new DownloadFolderScan("library-tv", "TV", Tv, entries)],
            [], [Grabbed], [], Now,
            excludePatterns: OrphanedDownloads.ParseExcludePatterns("*.nfo, Thumbs.db"));

        Assert.Equal("Left.Behind.S01E01", Assert.Single(found).Name);
    }

    [Fact]
    public void An_empty_skip_list_and_no_min_age_behave_exactly_as_before()
    {
        var entries = new[] { Entry($@"{Tv}\Show.S01E01.nfo"), Entry($@"{Tv}\Just.Finished", age: TimeSpan.FromMinutes(90)) };

        var found = OrphanedDownloads.Find([new DownloadFolderScan("library-tv", "TV", Tv, entries)], [], [Grabbed], [], Now);

        Assert.Equal("Show.S01E01.nfo", Assert.Single(found).Name);
    }

    [Fact]
    public void The_min_age_is_configurable()
    {
        var entries = new[] { Entry($@"{Tv}\Recent.Enough", age: TimeSpan.FromHours(3)) };
        var scans = new[] { new DownloadFolderScan("library-tv", "TV", Tv, entries) };

        Assert.Single(OrphanedDownloads.Find(scans, [], [Grabbed], [], Now, minAge: TimeSpan.FromHours(2)));
        Assert.Empty(OrphanedDownloads.Find(scans, [], [Grabbed], [], Now, minAge: TimeSpan.FromHours(6)));
    }

    [Theory]
    [InlineData("*.nfo", "movie.NFO", true)]
    [InlineData("Thumbs.db", "Thumbs.db", true)]
    [InlineData("Thumbs.db", "Thumbsxdb", false)]
    [InlineData("Sample?", "Sample1", true)]
    [InlineData("Sample?", "Sample12", false)]
    [InlineData("a+b", "a+b", true)]
    public void A_glob_matches_the_whole_name_ignoring_case(string pattern, string name, bool expected)
        => Assert.Equal(expected, OrphanedDownloads.MatchesGlob(pattern, name));

    [Fact]
    public void The_skip_list_is_read_from_commas_semicolons_and_new_lines()
        => Assert.Equal(["*.nfo", "Thumbs.db", ".DS_Store"], OrphanedDownloads.ParseExcludePatterns("*.nfo,\nThumbs.db ; .DS_Store\r\n*.NFO"));

    private static IReadOnlyList<OrphanedDownloadItem> Find(
        IReadOnlyList<DownloadFolderEntry> entries,
        IReadOnlyList<string> claimed,
        IReadOnlyList<string>? kept = null)
        => OrphanedDownloads.Find(
            [new DownloadFolderScan("library-tv", "TV", Tv, entries)],
            claimed,
            [Grabbed],
            kept ?? [],
            Now);

    private static DownloadFolderEntry Entry(string path, TimeSpan? age = null)
        => new(path, IsDirectory: true, 0, Now - (age ?? TimeSpan.FromDays(8)));
}
