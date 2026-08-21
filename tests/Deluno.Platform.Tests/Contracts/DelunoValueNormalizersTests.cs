using Deluno.Contracts;

namespace Deluno.Platform.Tests.Contracts;

public sealed class DelunoValueNormalizersTests
{
    [Theory]
    [InlineData(0, 24)]
    [InlineData(720, 720)]
    [InlineData(8760, 8760)]
    [InlineData(9000, 8760)]
    public void NormalizeSyncIntervalHours_uses_the_one_year_range(int input, int expected)
    {
        Assert.Equal(expected, DelunoValueNormalizers.NormalizeSyncIntervalHours(input));
    }
}
