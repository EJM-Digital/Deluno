using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// What the daily database check found (#1335). The check writes its result to the small note beside the databases; the
/// System screen already read it, but Health did not, so a database that failed its check was on one screen and not the other.
/// Reports nothing until a check has run.
/// </summary>
public sealed class DatabaseIntegrityHealthCheck(IOptions<StoragePathOptions> storageOptions) : IHealthCheck
{
    internal const string Id = "system:database-integrity";
    private const string Name = "Deluno's databases";

    public string Category => HealthCheckCategories.System;

    public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var maintenance = DatabaseMaintenanceLog.Read(Path.GetFullPath(storageOptions.Value.DataRoot));
        IReadOnlyList<HealthCheckOutcome> result = maintenance.IntegrityOk switch
        {
            null => [],
            true => [HealthCheckOutcome.Ok(Id, Category, Name, "Every database passed its daily check.")],
            false => [HealthCheckOutcome.Problem(
                Id, Category, Name,
                string.IsNullOrWhiteSpace(maintenance.IntegrityNote)
                    ? "A database did not pass its daily check."
                    : maintenance.IntegrityNote!,
                cause: "A database file is damaged. This can follow a power cut, a full drive or a failing disk.",
                fix: "Restore the last backup from System, Backups. If there is none, keep a copy of the data folder and ask for help before changing anything.")]
        };
        return Task.FromResult(result);
    }
}
