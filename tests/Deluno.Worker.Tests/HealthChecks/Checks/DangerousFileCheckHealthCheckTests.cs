using Deluno.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #944: while the content blocklist's dangerous-file check is off, System ->
/// Health shows one clear item with a one-click Fix; once it is on, the
/// check clears. Same shape as <see cref="AllowedHostsHealthCheckTests"/>.
/// </summary>
public sealed class DangerousFileCheckHealthCheckTests
{
    [Fact]
    public async Task Off_reports_a_suggestion_with_a_fix()
    {
        var repository = new Mock<IDownloadHealthRepository>();
        repository.Setup(repo => repo.GetContentBlocklistSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentBlocklistSettings.Default);
        var check = new DangerousFileCheckHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(DangerousFileCheckHealthCheck.Id, outcome.Id);
        Assert.Equal(HealthCheckStatuses.Suggestion, outcome.Status);
        Assert.Equal(HealthCheckCategories.System, outcome.Category);
        Assert.Contains("dangerous or junk", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(outcome.Fix);
    }

    [Fact]
    public async Task On_reports_ok()
    {
        var repository = new Mock<IDownloadHealthRepository>();
        repository.Setup(repo => repo.GetContentBlocklistSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentBlocklistSettings.Default with { Enabled = true });
        var check = new DangerousFileCheckHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
    }
}
