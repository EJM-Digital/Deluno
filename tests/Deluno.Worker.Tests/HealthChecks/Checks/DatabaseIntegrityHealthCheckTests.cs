using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#1335: what the daily database check found is on Health, not only on System.</summary>
public sealed class DatabaseIntegrityHealthCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-dbhealth-" + Guid.NewGuid().ToString("N"));

    public DatabaseIntegrityHealthCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private DatabaseIntegrityHealthCheck Check()
        => new(Options.Create(new StoragePathOptions { DataRoot = _root }));

    [Fact]
    public async Task Nothing_is_said_until_a_check_has_run()
    {
        Assert.Empty(await Check().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_database_that_failed_its_check_is_a_problem_naming_it_with_what_to_do()
    {
        DatabaseMaintenanceLog.RecordIntegrity(_root, DateTimeOffset.UtcNow, false, "Problem found in: Movies.");

        var outcome = Assert.Single(await Check().RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("Movies", outcome.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Cause));
        Assert.Contains("backup", outcome.Fix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_check_that_passed_is_ok_and_a_later_pass_clears_the_problem()
    {
        DatabaseMaintenanceLog.RecordIntegrity(_root, DateTimeOffset.UtcNow, false, "Problem found in: Movies.");
        DatabaseMaintenanceLog.RecordIntegrity(_root, DateTimeOffset.UtcNow, true, null);

        var outcome = Assert.Single(await Check().RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
    }
}
