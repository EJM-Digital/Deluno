using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#1067: another title in the folder is found whatever its letter case or separators, and whether it is a movie or a show.</summary>
public sealed class TitleFolderNeighboursTests
{
    private static async Task<TestStorage> SeedAsync()
    {
        var storage = TestStorage.Create();
        await Run(storage, DelunoDatabaseNames.Movies, """
            CREATE TABLE movie_wanted_state (movie_id TEXT, file_path TEXT);
            INSERT INTO movie_wanted_state VALUES ('m-dune', 'D:\Films\Dune\Dune.mkv'), ('m-me', 'D:\Films\Me\Me.mkv'), ('m-unc', '\\nas\films\Alien\Alien.mkv');
            """);
        await Run(storage, DelunoDatabaseNames.Series, """
            CREATE TABLE episode_entries (series_id TEXT, has_file INTEGER, file_path TEXT);
            INSERT INTO episode_entries VALUES ('s-wire', 1, '/tv/The Wire/Season 1/e1.mkv'), ('s-gone', 0, '/tv/Gone/e1.mkv');
            """);
        return storage;
    }

    private static async Task Run(TestStorage storage, string database, string sql)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(database, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(@"d:\films\dune")]
    [InlineData(@"D:\FILMS\DUNE\")]
    [InlineData("D:/Films/Dune/")]
    public async Task Another_movie_is_found_whatever_the_case_or_separators(string folder)
    {
        using var storage = await SeedAsync();
        Assert.True(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-me", null, folder, default));
    }

    [Fact]
    public async Task A_titles_own_files_and_a_lookalike_prefix_are_not_neighbours()
    {
        using var storage = await SeedAsync();
        Assert.False(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-dune", null, @"D:\Films\Dune", default));
        Assert.False(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-me", null, @"D:\Films\Dun", default));
    }

    [Fact]
    public async Task A_show_in_the_folder_counts_for_a_movie_and_a_movie_for_a_show()
    {
        using var storage = await SeedAsync();
        Assert.True(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-me", null, "/TV/the wire", default));
        Assert.True(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, null, "s-wire", @"D:\Films\Dune", default));
        Assert.False(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, null, "s-wire", "/tv/The Wire", default));
    }

    [Theory]
    [InlineData("/Films/Édith Piaf (2007)")]
    [InlineData("/films/édith piaf (2007)")]
    [InlineData(@"\FILMS\ÉDITH PIAF (2007)\")]
    public async Task A_neighbour_in_a_folder_with_an_uppercase_non_ASCII_letter_is_found_whatever_the_case(string folder)
    {
        using var storage = await SeedAsync();
        await Run(storage, DelunoDatabaseNames.Movies,
            "INSERT INTO movie_wanted_state VALUES ('m-edith', '/Films/Édith Piaf (2007)/Edith.mkv');");
        await Run(storage, DelunoDatabaseNames.Series,
            "INSERT INTO episode_entries VALUES ('s-emile', 1, '/tv/Émile/e1.mkv');");

        Assert.True(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-me", null, folder, default));
        Assert.True(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-me", null, "/tv/éMILE", default));
        Assert.False(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-edith", null, folder, default));
    }

    [Fact]
    public async Task A_unc_folder_is_matched_and_a_show_without_a_file_is_ignored()
    {
        using var storage = await SeedAsync();
        Assert.True(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-me", null, @"\\NAS\Films\Alien\", default));
        Assert.False(await TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(storage.Factory, "m-me", null, "/tv/Gone", default));
    }
}
