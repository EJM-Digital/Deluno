using Deluno.Contracts;

namespace Deluno.Integrations.Tests;

public sealed class SpeedTextTests
{
    [Theory]
    [InlineData(0d, "0 KB/s")]
    [InlineData(0.001d, "1 KB/s")]
    [InlineData(0.12d, "120 KB/s")]
    [InlineData(0.999d, "999 KB/s")]
    [InlineData(1d, "1.0 MB/s")]
    [InlineData(3.44d, "3.4 MB/s")]
    [InlineData(9.95d, "10 MB/s")]
    [InlineData(10d, "10 MB/s")]
    [InlineData(42.4d, "42 MB/s")]
    [InlineData(999d, "999 MB/s")]
    [InlineData(1000d, "1.0 GB/s")]
    [InlineData(1100d, "1.1 GB/s")]
    [InlineData(double.NaN, "0 KB/s")]
    [InlineData(-4d, "0 KB/s")]
    public void A_speed_reads_in_the_unit_that_fits_it(double mbps, string expected) =>
        Assert.Equal(expected, SpeedText.FromMbps(mbps));

    [Theory]
    [InlineData(75d, "75 KB/s")]
    [InlineData(2000d, "2.0 MB/s")]
    public void A_limit_saved_in_kilobytes_reads_the_same_way(double kbps, string expected) =>
        Assert.Equal(expected, SpeedText.FromKbps(kbps));

    [Fact]
    public void No_other_source_file_builds_a_speed_string_by_hand()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Deluno.Contracts")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var offenders = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => Path.GetFileName(file) != "SpeedText.cs")
            .Where(file => File.ReadLines(file)
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .Any(line => System.Text.RegularExpressions.Regex.IsMatch(line, "\"[^\"]*[KMG]B/s")))
            .Select(file => Path.GetRelativePath(root.FullName, file))
            .ToList();
        Assert.Empty(offenders);
    }
}
