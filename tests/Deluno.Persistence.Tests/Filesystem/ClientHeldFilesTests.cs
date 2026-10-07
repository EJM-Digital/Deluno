using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Filesystem;

public sealed class ClientHeldFilesTests
{
    [Theory]
    [InlineData(@"C:\Downloads\Completed\TV\Show.S01E05\Show.S01E05.mkv", @"C:\Downloads\Completed\TV\Show.S01E05\Show.S01E05.mkv", true)]
    [InlineData(@"C:\Downloads\Completed\TV\Show.S01E05", @"C:\Downloads\Completed\TV\Show.S01E05\Show.S01E05.mkv", true)]
    [InlineData(@"c:/downloads/completed/tv/show.s01e05/", @"C:\Downloads\Completed\TV\Show.S01E05\Show.S01E05.mkv", true)]
    [InlineData(@"C:\Downloads\Completed\TV\Show.S01E06", @"C:\Downloads\Completed\TV\Show.S01E05\Show.S01E05.mkv", false)]
    [InlineData(@"C:\Downloads\Completed\TV\Show.S01E0", @"C:\Downloads\Completed\TV\Show.S01E05\Show.S01E05.mkv", false)]
    [InlineData("", @"C:\Downloads\a.mkv", false)]
    public void A_client_claims_a_file_when_it_lists_the_file_or_its_folder(string claimed, string file, bool expected)
        => Assert.Equal(expected, ClientHeldFiles.Claims(claimed, file));
}
