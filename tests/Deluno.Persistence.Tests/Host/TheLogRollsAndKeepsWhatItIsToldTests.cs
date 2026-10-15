using Deluno.Infrastructure.Observability;

namespace Deluno.Persistence.Tests.Host;

/// <summary>Platform DAT-13: the log's rotation and retention defaults.</summary>
public sealed class TheLogRollsAndKeepsWhatItIsToldTests
{
    [Fact]
    public void The_log_rolls_at_50_MB_keeps_14_days_and_caps_the_folder_at_250_MB_unless_told_otherwise()
    {
        var defaults = new DelunoFileLogOptions();

        Assert.True(defaults.Enabled);
        Assert.Equal(50L * 1024 * 1024, defaults.MaxFileBytes);
        Assert.Equal(14, defaults.RetainDays);
        Assert.Equal(250L * 1024 * 1024, defaults.MaxTotalBytes);
        Assert.Equal("Logging:File", DelunoFileLogOptions.SectionName);
    }
}
