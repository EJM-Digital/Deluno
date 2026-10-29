using Deluno.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A download whose import failed is a critical finding that names where the import was reading from, or says that
/// no path was recorded (coverage register, area engines).
/// </summary>
public sealed class AnImportFailedDownloadIsAFindingTests
{
    private static RecoveryQueueSnapshot Item(string status, string? sourcePath)
        => new(status, null, sourcePath, 0, DateTimeOffset.UnixEpoch, 0, "Dune.2021.1080p.WEB-DL-DELUNO");

    [Fact]
    public void An_import_failed_item_is_a_critical_finding_naming_its_source_path()
    {
        var findings = new DownloadHealthEvaluator().Evaluate(Item("importFailed", "/downloads/Dune.2021"), DateTimeOffset.UnixEpoch.AddHours(1));

        var finding = Assert.Single(findings, item => item.Kind == "import-failed");
        Assert.Equal("critical", finding.Severity);
        Assert.Equal("Import source: /downloads/Dune.2021", finding.Evidence);
        Assert.True(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
    }

    [Fact]
    public void An_import_failed_item_with_no_recorded_path_says_so_and_a_healthy_item_has_no_such_finding()
    {
        var failed = new DownloadHealthEvaluator().Evaluate(Item("importFailed", null), DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.Equal("No import source path was recorded.", Assert.Single(failed, item => item.Kind == "import-failed").Evidence);

        var imported = new DownloadHealthEvaluator().Evaluate(Item("imported", "/downloads/Dune.2021"), DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.DoesNotContain(imported, item => item.Kind == "import-failed");
    }
}
